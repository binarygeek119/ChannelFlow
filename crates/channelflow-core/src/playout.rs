//! Turning ChannelFlow's channels into ErsatzTV-next's `lineup.json` and
//! per-channel `channel.json`, so the encoder can play the scheduler's playout
//! JSON. The playout documents themselves are the scheduler plugin's job; this
//! module only points next at them.

use std::path::{Path, PathBuf};

use serde::Serialize;
use serde_json::{json, Value};

use crate::model::Channel;

/// The lineup schema this writer targets.
pub const LINEUP_SCHEMA: &str = "https://ersatztv.org/lineup/version/0.0.1";
/// The channel schema this writer targets.
pub const CHANNEL_SCHEMA: &str = "https://ersatztv.org/channel/version/0.1.1";

/// Where the encoder and its outputs live. Defaults keep everything under
/// `<config>/next` so one directory can be mounted into the container.
#[derive(Debug, Clone)]
pub struct EncoderSettings {
    pub bind_address: String,
    pub port: u16,
    /// `<config>/next` — where `lineup.json` and `channels/` are written.
    pub next_root: PathBuf,
    /// Where the encoder writes HLS segments.
    pub hls_folder: PathBuf,
    /// Where the encoder writes its own XMLTV.
    pub xmltv_folder: PathBuf,
}

impl EncoderSettings {
    pub fn new(config_root: &Path) -> Self {
        let next_root = config_root.join("next");
        Self {
            bind_address: "0.0.0.0".to_string(),
            port: 8409,
            hls_folder: next_root.join("hls"),
            xmltv_folder: next_root.join("xmltv"),
            next_root,
        }
    }
}

/// The `{number}` used in file names and the lineup: whole numbers stay whole.
pub fn number_string(number: f64) -> String {
    if number.fract() == 0.0 && number.is_finite() {
        format!("{}", number as i64)
    } else {
        format!("{number}")
    }
}

/// next's `lineup.json`, one entry per enabled channel.
pub fn lineup_document(channels: &[Channel], encoder: &EncoderSettings) -> Value {
    let entries: Vec<Value> = channels
        .iter()
        .filter(|channel| channel.enabled)
        .map(|channel| {
            let number = number_string(channel.number);
            json!({
                "number": number,
                "name": channel.name,
                "config": format!("./channels/{number}.json"),
                "tvg_id": format!("channelflow.{number}"),
                "group": "ChannelFlow",
            })
        })
        .collect();
    json!({
        "version": LINEUP_SCHEMA,
        "server": { "bind_address": encoder.bind_address, "port": encoder.port },
        "output": { "folder": encoder.hls_folder.to_string_lossy() },
        "xmltv": { "folder": encoder.xmltv_folder.to_string_lossy() },
        "channels": entries,
    })
}

/// next's per-channel `channel.json`, with `playout.folder` pointing at the
/// scheduler's output for that channel.
pub fn channel_document(playout_folder: &Path) -> Value {
    json!({
        "version": CHANNEL_SCHEMA,
        "playout": { "folder": playout_folder.to_string_lossy() },
        "ffmpeg": { "ffmpeg_path": "", "ffprobe_path": "", "disabled_filters": [] },
        "normalization": {
            "audio": {
                "mode": "transcode",
                "format": "aac",
                "bitrate_kbps": 192,
                "channels": 2,
                "sample_rate_hz": 48000,
            },
            "video": {
                "mode": "transcode",
                "format": "h264",
                "bit_depth": 8,
                "width": 1920,
                "height": 1080,
                "bitrate_kbps": 2000,
            },
            "subtitle": { "mode": "burn" },
        },
    })
}

/// What one write produced.
#[derive(Debug, Clone, Serialize)]
pub struct PlayoutPaths {
    pub lineup: String,
    pub channels: Vec<String>,
}

/// An absolute form of `path`, so next (which resolves relative paths against
/// its own config dir) reads the folders we mean.
fn absolute(path: &Path) -> PathBuf {
    let joined = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .map(|cwd| cwd.join(path))
            .unwrap_or_else(|_| path.to_path_buf())
    };
    // Drop `.` components so the paths next reads are clean.
    let mut out = PathBuf::new();
    for component in joined.components() {
        match component {
            std::path::Component::CurDir => {}
            other => out.push(other.as_os_str()),
        }
    }
    out
}

/// Write `<config>/next/lineup.json` and every enabled channel's config.
/// `playout_base` is the folder the scheduler writes playout JSON under; each
/// channel gets a subfolder named by its id.
pub fn write(
    config_root: &Path,
    channels: &[Channel],
    playout_base: &Path,
) -> Result<PlayoutPaths, String> {
    let config_root = absolute(config_root);
    let playout_base = absolute(playout_base);
    let encoder = EncoderSettings::new(&config_root);
    let channels_dir = encoder.next_root.join("channels");
    for dir in [&channels_dir, &encoder.hls_folder, &encoder.xmltv_folder] {
        std::fs::create_dir_all(dir).map_err(|error| error.to_string())?;
    }

    let mut written = Vec::new();
    for channel in channels.iter().filter(|channel| channel.enabled) {
        let number = number_string(channel.number);
        let playout_folder = playout_base.join(channel.id.to_string());
        let document = channel_document(&playout_folder);
        let path = channels_dir.join(format!("{number}.json"));
        let text = serde_json::to_string_pretty(&document).map_err(|error| error.to_string())?;
        std::fs::write(&path, text).map_err(|error| error.to_string())?;
        written.push(path.to_string_lossy().to_string());
    }

    let lineup = lineup_document(channels, &encoder);
    let lineup_path = encoder.next_root.join("lineup.json");
    let text = serde_json::to_string_pretty(&lineup).map_err(|error| error.to_string())?;
    std::fs::write(&lineup_path, text).map_err(|error| error.to_string())?;

    Ok(PlayoutPaths {
        lineup: lineup_path.to_string_lossy().to_string(),
        channels: written,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::Utc;
    use uuid::Uuid;

    fn channel(number: f64, name: &str, enabled: bool) -> Channel {
        Channel {
            id: Uuid::new_v4(),
            number,
            name: name.to_string(),
            description: String::new(),
            enabled,
            watermark: None,
            ai_prompt: String::new(),
            transcode: json!({}),
            created_at: Utc::now(),
            updated_at: Utc::now(),
        }
    }

    #[test]
    fn the_lineup_carries_only_enabled_channels_with_nexts_keys() {
        let channels = vec![channel(1.0, "One", true), channel(2.5, "Two", false)];
        let encoder = EncoderSettings::new(Path::new("/config"));
        let lineup = lineup_document(&channels, &encoder);
        assert_eq!(lineup["version"], LINEUP_SCHEMA);
        assert!(lineup["server"]["port"].is_number());
        let entries = lineup["channels"].as_array().expect("channels");
        assert_eq!(entries.len(), 1, "only enabled channels");
        assert_eq!(entries[0]["number"], "1");
        assert_eq!(entries[0]["config"], "./channels/1.json");
        assert_eq!(entries[0]["tvg_id"], "channelflow.1");
    }

    #[test]
    fn a_channel_config_points_at_the_scheduler_playout_folder() {
        let document = channel_document(Path::new("/config/playout/abc"));
        assert_eq!(document["version"], CHANNEL_SCHEMA);
        assert_eq!(document["playout"]["folder"], "/config/playout/abc");
        assert_eq!(document["normalization"]["video"]["format"], "h264");
    }

    #[test]
    fn writing_creates_the_lineup_and_each_enabled_channel() {
        let root = std::env::temp_dir().join(format!("channelflow-playout-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let channels = vec![channel(7.0, "Seven", true), channel(8.0, "Eight", false)];
        let playout_base = root.join("playout");
        let paths = write(&root, &channels, &playout_base).expect("write");
        assert!(Path::new(&paths.lineup).exists());
        assert_eq!(paths.channels.len(), 1, "only the enabled channel");
        let channel_config = std::fs::read_to_string(&paths.channels[0]).expect("read");
        let value: Value = serde_json::from_str(&channel_config).expect("json");
        assert!(value["playout"]["folder"]
            .as_str()
            .unwrap()
            .ends_with(&channels[0].id.to_string()));
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn whole_numbers_stay_whole() {
        assert_eq!(number_string(1.0), "1");
        assert_eq!(number_string(119.1), "119.1");
    }
}
