//! ChannelFlow TV client logs.
//!
//! TV apps ship their log lines back to the server through an API-keyed
//! endpoint; the lines land in `<config>/clients/<deviceId>/` as one dated
//! file per day, next to a `device.json` describing the app. This module is
//! the v2 port of v1.0.0's `ClientLogStore`.

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

use chrono::{Datelike, Utc};
use serde::Deserialize;

const FOLDER: &str = "clients";
const FILE_PREFIX: &str = "channelflow-client-";
const METADATA_FILE: &str = "device.json";
const MAX_ENTRIES: usize = 200;
const MAX_MESSAGE_CHARS: usize = 8_000;
const MAX_EXCEPTION_CHARS: usize = 16_000;
const MAX_DEVICE_FILES_BYTES: u64 = 16 * 1024 * 1024; // refuse a runaway folder
const KEEP_DAYS: i64 = 7;

/// One `POST /api/client-logs` body from a TV app (v1.0.0's wire format:
/// camelCase field names).
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IngestRequest {
    #[serde(default)]
    pub device_id: Option<String>,
    #[serde(default)]
    pub device_name: Option<String>,
    #[serde(default)]
    pub app_version: Option<String>,
    #[serde(default)]
    pub os_version: Option<String>,
    #[serde(default)]
    pub entries: Vec<Entry>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct Entry {
    #[serde(default)]
    pub timestamp: Option<String>,
    #[serde(default)]
    pub level: Option<String>,
    #[serde(default)]
    pub tag: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub exception: Option<String>,
}

/// What `ingest` accepted.
pub struct Ingested {
    pub accepted: usize,
    pub device_id: String,
}

/// `<config>/clients`.
pub fn root(config_dir: &Path) -> PathBuf {
    config_dir.join(FOLDER)
}

/// A filesystem-safe device id (letters, digits, `.`, `_`, `-`, ≤64).
pub fn sanitize_device_id(value: &str) -> Option<String> {
    let cleaned: String = value
        .trim()
        .chars()
        .filter(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-'))
        .collect();
    if cleaned.is_empty() {
        return None;
    }
    Some(if cleaned.len() > 64 {
        cleaned[..64].to_string()
    } else {
        cleaned
    })
}

fn truncate(value: &str, max: usize) -> String {
    let mut chars = value.chars();
    let mut out = String::new();
    for (i, c) in chars.by_ref().enumerate() {
        if i >= max {
            break;
        }
        out.push(c);
    }
    out
}

/// Append a client's log lines to its dated file. Refuses bad payloads, and
/// prunes the folder's old files so disks do not fill.
pub fn ingest(config_dir: &Path, request: &IngestRequest) -> Result<Ingested, String> {
    let device_id = request
        .device_id
        .as_deref()
        .map(sanitize_device_id)
        .flatten()
        .ok_or_else(|| "deviceId is required".to_string())?;
    let mut entries = request.entries.clone();
    if entries.is_empty() {
        return Err("at least one log entry is required".to_string());
    }
    if entries.len() > MAX_ENTRIES {
        entries.truncate(MAX_ENTRIES);
    }

    let now = Utc::now();
    let day = format!("{:04}{:02}{:02}", now.year(), now.month(), now.day());
    let dir = root(config_dir).join(&device_id);
    fs_create_dir_all(&dir).map_err(|error| format!("could not create the client folder: {error}"))?;

    let mut accepted = 0usize;
    let mut buffer = Vec::new();
    for entry in &entries {
        let message = truncate(entry.message.as_deref().unwrap_or(""), MAX_MESSAGE_CHARS);
        if message.is_empty() && entry.exception.as_deref().unwrap_or("").is_empty() {
            continue;
        }
        let timestamp = entry
            .timestamp
            .as_deref()
            .filter(|value| !value.is_empty())
            .map(str::to_string)
            .unwrap_or_else(|| now.to_rfc3339());
        let level = entry
            .level
            .as_deref()
            .filter(|value| !value.is_empty())
            .unwrap_or("INFO")
            .to_uppercase();
        let tag = truncate(entry.tag.as_deref().unwrap_or(""), 80);
        let _ = writeln!(
            buffer,
            "{timestamp} [{level}] {tag}{message}",
            tag = if tag.is_empty() { String::new() } else { format!("{tag}: ") },
            message = message.replace('\r', " ").replace('\n', " "),
        );
        let exception = truncate(entry.exception.as_deref().unwrap_or(""), MAX_EXCEPTION_CHARS);
        if !exception.is_empty() {
            let _ = writeln!(buffer, "{exception}");
        }
        accepted += 1;
    }
    if accepted == 0 {
        return Err("at least one log entry is required".to_string());
    }

    let meta_path = dir.join(METADATA_FILE);
    let metadata = serde_json::json!({
        "device_id": device_id,
        "device_name": truncate(request.device_name.as_deref().unwrap_or(""), 80),
        "app_version": truncate(request.app_version.as_deref().unwrap_or(""), 40),
        "os_version": truncate(request.os_version.as_deref().unwrap_or(""), 80),
        "last_seen_at": now.to_rfc3339(),
    });
    let _ = fs::write(&meta_path, serde_json::to_vec_pretty(&metadata).unwrap_or_default());

    let file = dir.join(format!("{FILE_PREFIX}{day}.log"));
    let mut open = fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&file)
        .map_err(|error| format!("could not open the client log: {error}"))?;
    open.write_all(&buffer)
        .map_err(|error| format!("could not write the client log: {error}"))?;

    purge(dir);
    Ok(Ingested {
        accepted,
        device_id,
    })
}

fn purge(dir: PathBuf) {
    // Keep the folder from growing without bound.
    if dir_total_bytes(&dir).unwrap_or(0) > MAX_DEVICE_FILES_BYTES {
        let _ = fs::remove_file(&dir);
        return;
    }
    let today = Utc::now().date_naive();
    if let Ok(read) = fs::read_dir(&dir) {
        for entry in read.flatten() {
            let name = entry.file_name().to_string_lossy().to_string();
            if !name.starts_with(FILE_PREFIX) || !name.ends_with(".log") {
                continue;
            }
            let digits = name.trim_start_matches(FILE_PREFIX).trim_end_matches(".log");
            if digits.len() == 8 {
                if let Ok(day) = chrono::NaiveDate::parse_from_str(digits, "%Y%m%d") {
                    let age = (today - day).num_days();
                    if age > KEEP_DAYS {
                        let _ = fs::remove_file(dir.join(&name));
                    }
                }
            }
        }
    }
}

fn dir_total_bytes(dir: &Path) -> Result<u64, ()> {
    let mut total = 0u64;
    for entry in fs::read_dir(dir).map_err(|_| ())? {
        if let Ok(entry) = entry {
            if let Ok(meta) = entry.metadata() {
                total += meta.len();
            }
        }
    }
    Ok(total)
}

fn fs_create_dir_all(dir: &Path) -> std::io::Result<()> {
    fs::create_dir_all(dir)
}

fn read_device_meta(dir: &Path) -> Option<serde_json::Value> {
    fs::read(dir.join(METADATA_FILE))
        .ok()
        .and_then(|bytes| serde_json::from_slice(&bytes).ok())
}

/// The devices that have sent logs (newest first).
pub fn list_devices(config_dir: &Path) -> Vec<serde_json::Value> {
    let root = root(config_dir);
    let Ok(read) = fs::read_dir(&root) else {
        return Vec::new();
    };
    let mut devices = Vec::new();
    for entry in read.flatten() {
        if !entry.path().is_dir() {
            continue;
        }
        let device_id = entry.file_name().to_string_lossy().to_string();
        if sanitize_device_id(&device_id).as_deref() != Some(device_id.as_str()) {
            continue;
        }
        let meta = read_device_meta(&entry.path());
        let files = list_files(&entry.path());
        let latest = files.first().cloned();
        let last_seen = meta
            .as_ref()
            .and_then(|m| m["last_seen_at"].as_str().map(str::to_string))
            .or_else(|| latest.as_ref().and_then(|f| f["written_at"].as_str().map(str::to_string)));
        devices.push(serde_json::json!({
            "device_id": device_id,
            "device_name": meta.as_ref().and_then(|m| m["device_name"].as_str()),
            "app_version": meta.as_ref().and_then(|m| m["app_version"].as_str()),
            "os_version": meta.as_ref().and_then(|m| m["os_version"].as_str()),
            "last_seen_at": last_seen,
            "latest_file": latest.as_ref().and_then(|f| f["name"].as_str()),
            "total_bytes": files.iter().map(|f| f["bytes"].as_u64().unwrap_or(0)).sum::<u64>(),
            "file_count": files.len(),
        }));
    }
    devices.sort_by(|a, b| {
        b["last_seen_at"]
            .as_str()
            .unwrap_or("")
            .cmp(a["last_seen_at"].as_str().unwrap_or(""))
    });
    devices
}

fn list_files(dir: &Path) -> Vec<serde_json::Value> {
    let mut files = Vec::new();
    if let Ok(read) = fs::read_dir(dir) {
        for entry in read.flatten() {
            let name = entry.file_name().to_string_lossy().to_string();
            if !name.starts_with(FILE_PREFIX) || !name.ends_with(".log") {
                continue;
            }
            if let Ok(meta) = entry.metadata() {
                let written_at = meta
                    .modified()
                    .ok()
                    .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
                    .map(|duration| {
                        chrono::DateTime::from_timestamp(duration.as_secs() as i64, 0)
                            .unwrap_or_else(Utc::now)
                            .to_rfc3339()
                    });
                files.push(serde_json::json!({
                    "name": name,
                    "bytes": meta.len(),
                    "written_at": written_at,
                }));
            }
        }
    }
    files.sort_by(|a, b| b["name"].as_str().unwrap_or("").cmp(a["name"].as_str().unwrap_or("")));
    files
}

/// One device's log detail: metadata, available files, and the tail.
pub fn device_detail(
    config_dir: &Path,
    device_id: &str,
    file: Option<&str>,
    tail_bytes: Option<i64>,
) -> Option<serde_json::Value> {
    let id = sanitize_device_id(device_id)?;
    let dir = root(config_dir).join(&id);
    if !dir.is_dir() {
        return None;
    }
    let files = list_files(&dir);
    let chosen = match file {
        Some(name) if files.iter().any(|f| f["name"].as_str() == Some(name)) => {
            files.iter().find(|f| f["name"].as_str() == Some(name)).cloned()
        }
        Some(_) => return None,
        None => files.first().cloned(),
    };
    let content = chosen
        .as_ref()
        .and_then(|f| {
            let path = dir.join(f["name"].as_str().unwrap_or(""));
            let max = tail_bytes.unwrap_or(131_072).clamp(1_024, 1_048_576) as u64;
            read_tail(&path, max).ok()
        })
        .unwrap_or_default();
    let meta = read_device_meta(&dir);
    Some(serde_json::json!({
        "device_id": id,
        "device_name": meta.as_ref().and_then(|m| m["device_name"].as_str()),
        "app_version": meta.as_ref().and_then(|m| m["app_version"].as_str()),
        "os_version": meta.as_ref().and_then(|m| m["os_version"].as_str()),
        "last_seen_at": meta.as_ref().and_then(|m| m["last_seen_at"].as_str()),
        "latest_file": chosen.as_ref().and_then(|f| f["name"].as_str()),
        "content": content,
        "files": files,
    }))
}

fn read_tail(path: &Path, max: u64) -> std::io::Result<String> {
    use std::io::{Read, Seek, SeekFrom};
    let mut open = fs::File::open(path)?;
    let size = open.metadata()?.len();
    if size > max {
        open.seek(SeekFrom::Start(size - max))?;
    }
    let mut bytes = Vec::new();
    open.read_to_end(&mut bytes)?;
    Ok(String::from_utf8_lossy(&bytes).into_owned())
}