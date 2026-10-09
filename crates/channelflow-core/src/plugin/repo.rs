//! The Jellyfin-style plugin repository: a `manifest.json` served from a plain
//! URL, listing plugins and the zips each released version ships, which the
//! base fetches to browse and install plugins without a rebuild.
//!
//! The manifest is Jellyfin-shaped on purpose — one entry per plugin carrying a
//! `versions[]` list. ChannelFlow adds `min_base_version`/`max_base_version`
//! to each version and an `artifacts` map (per platform `rid`) instead of a
//! single zip: the installer picks the artifact for the host's `rid`, verifies
//! its sha256 against the manifest, extracts it, and stages it under
//! `<config>/plugins/.installed/{id}`. Nothing here loads the library — the
//! manifest's `entrypoint` names the file a dynamic loader will reach for
//! once the SDK defines the ABI.

use std::collections::BTreeMap;
use std::io::Read;
use std::path::Path;

use channelflow_plugin_api::manifest::PluginManifest;
use chrono::Utc;
use serde::Deserialize;
use sha2::{Digest, Sha256};
use tokio::io::AsyncWriteExt;

use crate::store::{Store, StoreError};

/// One plugin's `manifest.json` entry.
#[derive(Debug, Clone, Deserialize)]
pub struct CatalogEntry {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub guid: String,
    #[serde(default, rename = "imageUrl")]
    pub image_url: String,
    #[serde(default)]
    pub description: String,
    #[serde(default)]
    pub owner: String,
    #[serde(default)]
    pub category: String,
    #[serde(default)]
    pub homepage: String,
    #[serde(default)]
    pub versions: Vec<CatalogVersion>,
}

/// One released version of a plugin.
#[derive(Debug, Clone, Deserialize)]
pub struct CatalogVersion {
    pub version: String,
    #[serde(default)]
    pub min_base_version: String,
    #[serde(default)]
    pub max_base_version: String,
    #[serde(default)]
    pub timestamp: String,
    /// Per-platform `rid` -> the zip that runs there.
    #[serde(default)]
    pub artifacts: BTreeMap<String, CatalogArtifact>,
}

/// One platform's install artifact.
#[derive(Debug, Clone, Deserialize)]
pub struct CatalogArtifact {
    pub url: String,
    /// `sha256:<hex>`; empty means "do not verify".
    #[serde(default)]
    pub checksum: String,
    #[serde(default)]
    pub size: u64,
}

/// The result of a completed install, shaped for the API and the record
/// the store keeps.
#[derive(Debug, Clone, serde::Serialize)]
pub struct InstalledPlugin {
    pub id: String,
    pub version: String,
    pub rid: String,
    pub repo: String,
    pub installed_at: String,
    /// `dir` holds the extracted plugin; `entrypoint` is the shared library
    /// the manifest names for `rid`, when the zip declared one.
    pub dir: String,
    pub entrypoint: Option<String>,
}

/// The `rid` of this host, matching the manifest's artifact keys, e.g.
/// `linux-x86_64`.
pub fn current_rid() -> String {
    let arch = match std::env::consts::ARCH {
        "x86_64" => "x86_64",
        "aarch64" | "arm64" => "aarch64",
        other => other,
    };
    format!("{}-{arch}", std::env::consts::OS)
}

/// Parse a repository manifest body into plugin entries.
pub fn parse_catalog(body: &str) -> Result<Vec<CatalogEntry>, StoreError> {
    let entries: Vec<CatalogEntry> = serde_json::from_str(body)
        .map_err(|error| StoreError::Plugin(format!("repository manifest is not valid: {error}")))?;
    if entries.is_empty() {
        return Err(StoreError::Plugin(
            "repository manifest lists no plugins".to_string(),
        ));
    }
    for entry in &entries {
        if entry.id.trim().is_empty() {
            return Err(StoreError::Plugin(
                "repository manifest has an entry without an id".to_string(),
            ));
        }
    }
    Ok(entries)
}

/// Fetch and parse a repository manifest over the shared HTTP client.
pub async fn fetch_catalog(
    client: &reqwest::Client,
    repository_url: &str,
) -> Result<Vec<CatalogEntry>, StoreError> {
    let response = client
        .get(repository_url)
        .send()
        .await
        .map_err(|error| {
            StoreError::Plugin(format!("fetching {repository_url}: {error}"))
        })?;
    if !response.status().is_success() {
        return Err(StoreError::Plugin(format!(
            "{repository_url} answered {}",
            response.status()
        )));
    }
    let body = response
        .text()
        .await
        .map_err(|error| StoreError::Plugin(format!("reading {repository_url}: {error}")))?;
    parse_catalog(&body)
}

/// The catalog view the API serves: repository-tagged, with compatibility
/// judged against `base_version` and the rids each version ships.
pub fn catalog_view(
    repository_url: &str,
    entries: &[CatalogEntry],
    base_version: &str,
) -> serde_json::Value {
    let plugins: Vec<serde_json::Value> = entries
        .iter()
        .map(|entry| {
            let compatible = entry.versions.iter().any(|version| {
                channelflow_plugin_api::version::compatible(
                    base_version,
                    &version.min_base_version,
                    &version.max_base_version,
                )
            });
            let name = if entry.name.is_empty() {
                entry.id.clone()
            } else {
                entry.name.clone()
            };
            serde_json::json!({
                "repository": repository_url,
                "id": entry.id,
                "guid": entry.guid,
                "image_url": entry.image_url,
                "name": name,
                "description": entry.description,
                "owner": entry.owner,
                "category": entry.category,
                "homepage": entry.homepage,
                "compatible": compatible,
                "versions": entry.versions.iter().map(|version| {
                    serde_json::json!({
                        "version": version.version,
                        "min_base_version": version.min_base_version,
                        "max_base_version": version.max_base_version,
                        "timestamp": version.timestamp,
                        "rids": version.artifacts.keys().collect::<Vec<_>>(),
                    })
                }).collect::<Vec<_>>(),
            })
        })
        .collect();
    serde_json::json!({ "plugins": plugins })
}

/// Pick the version to install: the one the caller named, or the newest
/// compatible with `base_version`.
fn pick_version(
    entry: &CatalogEntry,
    base_version: &str,
    want: Option<&str>,
) -> Result<CatalogVersion, StoreError> {
    let candidate = match want {
        Some(version) => entry
            .versions
            .iter()
            .find(|candidate| candidate.version == version)
            .ok_or_else(|| {
                StoreError::Plugin(format!(
                    "{} has no version {}",
                    entry.id, version
                ))
            }),
        None => entry
            .versions
            .iter()
            .rev()
            .find(|candidate| {
                channelflow_plugin_api::version::compatible(
                    base_version,
                    &candidate.min_base_version,
                    &candidate.max_base_version,
                )
            })
            .ok_or_else(|| {
                StoreError::Plugin(format!(
                    "no version of {} is compatible with base {}",
                    entry.id, base_version
                ))
            }),
    }?;
    if !channelflow_plugin_api::version::compatible(
        base_version,
        &candidate.min_base_version,
        &candidate.max_base_version,
    ) {
        return Err(StoreError::Plugin(format!(
            "{} {} requires a base between {} and {}, this is {}",
            entry.id,
            candidate.version,
            candidate.min_base_version,
            candidate.max_base_version,
            base_version
        )));
    }
    Ok(candidate.clone())
}

/// Download one artifact to `dest`, checking HTTP status and announced size.
async fn download_artifact(
    client: &reqwest::Client,
    artifact: &CatalogArtifact,
    dest: &Path,
) -> Result<(), StoreError> {
    if let Some(parent) = dest.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let mut response = client
        .get(&artifact.url)
        .send()
        .await
        .map_err(|error| StoreError::Plugin(format!("downloading {}: {error}", artifact.url)))?;
    if !response.status().is_success() {
        return Err(StoreError::Plugin(format!(
            "{} answered {}",
            artifact.url,
            response.status()
        )));
    }
    let mut file = tokio::fs::File::create(dest).await?;
    let mut written: u64 = 0;
    loop {
        let chunk = response
            .chunk()
            .await
            .map_err(|error| StoreError::Plugin(format!("downloading {}: {error}", artifact.url)))?;
        let Some(chunk) = chunk else { break };
        written += chunk.len() as u64;
        file.write_all(&chunk[..]).await?;
    }
    if artifact.size > 0 && written != artifact.size {
        return Err(StoreError::Plugin(format!(
            "{} is {written} bytes, the manifest promised {}",
            artifact.url, artifact.size
        )));
    }
    file.flush().await?;
    Ok(())
}

/// sha256 of a file in lowercase hex, or an io error.
fn sha256_of(dest: &Path) -> Result<String, std::io::Error> {
    let bytes = std::fs::read(dest)?;
    let digest = Sha256::digest(&bytes);
    let hex = digest.iter().map(|byte| format!("{byte:02x}")).collect::<String>();
    Ok(hex)
}

/// Verify a downloaded zip against the manifest's `sha256:` checksum.
fn verify_checksum(dest: &Path, expected: &str) -> Result<(), StoreError> {
    let expected = expected.trim();
    if expected.is_empty() {
        return Ok(());
    }
    let (scheme, want) = expected.split_once(':').unwrap_or(("plain", expected));
    if scheme != "sha256" {
        return Err(StoreError::Plugin(format!(
            "unsupported checksum scheme {scheme:?}; only sha256 is accepted"
        )));
    }
    let got = sha256_of(dest).map_err(StoreError::Io)?;
    if !got.eq_ignore_ascii_case(want) {
        return Err(StoreError::Plugin(format!(
            "checksum mismatch: the manifest says {want}, the download hashes to {got}"
        )));
    }
    Ok(())
}

/// Extract a zip into `dest`, refusing any entry that would escape it.
fn extract_zip(zip_path: &Path, dest: &Path) -> Result<(), StoreError> {
    let file = std::fs::File::open(zip_path)?;
    let mut archive = zip::ZipArchive::new(file)
        .map_err(|error| StoreError::Plugin(format!("{} is not a zip: {error}", zip_path.display())))?;
    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .map_err(|error| StoreError::Plugin(format!("reading the zip: {error}")))?;
        let relative = entry.enclosed_name().ok_or_else(|| {
            StoreError::Plugin(format!(
                "zip entry {} would escape the install directory",
                entry.name()
            ))
        })?;
        let out = dest.join(relative);
        if entry.is_dir() {
            std::fs::create_dir_all(&out)?;
            continue;
        }
        if let Some(parent) = out.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let mut bytes = Vec::new();
        entry
            .read_to_end(&mut bytes)
            .map_err(|error| StoreError::Plugin(format!("reading the zip: {error}")))?;
        std::fs::write(&out, bytes)?;
    }
    Ok(())
}

/// Fetch a repository, pick a compatible version of `id`, download its zip for
/// this host's `rid`, verify the sha256, extract it, and stage it as the
/// installed copy. The library is *not* loaded — loading needs the dynamic
/// loader and the SDK's ABI entrypoint, which come later; the manifest's
/// `entrypoint` field records the file for that moment.
pub async fn install(
    client: &reqwest::Client,
    store: &Store,
    repository_url: &str,
    id: &str,
    version: Option<&str>,
    rid: &str,
    base_version: &str,
) -> Result<InstalledPlugin, StoreError> {
    let entries = fetch_catalog(client, repository_url).await?;
    let entry = entries
        .iter()
        .find(|entry| entry.id == id)
        .ok_or_else(|| StoreError::Plugin(format!("{repository_url} has no plugin {id}")))?;
    let picked = pick_version(entry, base_version, version)?;
    let artifact = picked
        .artifacts
        .get(rid)
        .ok_or_else(|| {
            StoreError::Plugin(format!(
                "{} {} ships no build for {rid}; available: {}",
                id,
                picked.version,
                picked.artifacts.keys().cloned().collect::<Vec<_>>().join(", ")
            ))
        })?;

    let cache = store.cache_dir();
    let zip_path = cache.join(format!("{id}-{}-{rid}.zip", picked.version));
    download_artifact(client, artifact, &zip_path).await?;
    verify_checksum(&zip_path, &artifact.checksum)?;

    let staging = cache.join(format!("{id}-{}-{rid}.tmp", picked.version));
    if staging.exists() {
        std::fs::remove_dir_all(&staging)?;
    }
    extract_zip(&zip_path, &staging)?;

    // The zip must carry a plugin.json that passes the SDK's own manifest
    // validation — that is the artifact's signature of being a plugin.
    let manifest_path = staging.join("plugin.json");
    if !manifest_path.exists() {
        return Err(StoreError::Plugin(format!(
            "the {id} zip has no plugin.json"
        )));
    }
    let manifest_body = std::fs::read_to_string(&manifest_path)?;
    let manifest = PluginManifest::parse(&manifest_body)
        .map_err(|error| StoreError::Plugin(format!("installed plugin.json is invalid: {error}")))?;
    if manifest.id != id {
        return Err(StoreError::Plugin(format!(
            "the zip's plugin.json says {}, installing as {id}",
            manifest.id
        )));
    }
    let entrypoint = manifest.entrypoint.get(rid).cloned();
    if entrypoint.is_none() {
        tracing::warn!(
            plugin = %id,
            rid,
            "installed plugin declares no entrypoint for {rid}; it can only be \
             loaded once the dynamic loader and ABI entrypoint exist"
        );
    }

    // Swap the staged copy into place, replacing any previous install.
    let installed_dir = store.installed_dir(id);
    if installed_dir.exists() {
        std::fs::remove_dir_all(&installed_dir)?;
    }
    if let Some(parent) = installed_dir.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::rename(&staging, &installed_dir)?;

    let installed = InstalledPlugin {
        id: id.to_string(),
        version: picked.version.clone(),
        rid: rid.to_string(),
        repo: repository_url.to_string(),
        installed_at: Utc::now().to_rfc3339(),
        dir: installed_dir.display().to_string(),
        entrypoint,
    };
    store
        .record_installed(serde_json::to_value(&installed).map_err(StoreError::Json)?)
        .await?;
    tracing::info!(
        plugin = %id,
        version = %picked.version,
        rid,
        dir = %installed_dir.display(),
        "installed plugin"
    );
    Ok(installed)
}

/// Remove an installed plugin: its directory and its record. The compiled-in
/// copy, if any, is untouched.
pub async fn uninstall(store: &Store, id: &str) -> Result<serde_json::Value, StoreError> {
    let mut installed = store.installed_list().await?;
    let record = installed
        .iter()
        .find(|record| record["id"].as_str() == Some(id))
        .cloned()
        .ok_or_else(|| StoreError::Plugin(format!("no installed plugin with id {id}")))?;
    let dir = store.installed_dir(id);
    if dir.exists() {
        std::fs::remove_dir_all(&dir)?;
    }
    installed.retain(|record| record["id"].as_str() != Some(id));
    store.replace_installed(installed).await?;
    tracing::info!(plugin = %id, dir = %dir.display(), "uninstalled plugin");
    Ok(record)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::store::Store;
    use std::io::Write;

    const MANIFEST: &str = r#"[
        {
            "id": "com.channelflow.ai",
            "name": "AI Provider Suite",
            "guid": "com.channelflow.ai",
            "imageUrl": "https://example.invalid/ai.png",
            "description": "AI",
            "owner": "ChannelFlow Team",
            "category": "ai",
            "versions": [
                {
                    "version": "1.5.0",
                    "min_base_version": "1.0.0",
                    "max_base_version": "1.999.999",
                    "artifacts": {
                        "linux-x86_64": {
                            "url": "https://example.invalid/ai-1.5.0.zip",
                            "checksum": "sha256:0",
                            "size": 1024
                        }
                    }
                },
                {
                    "version": "2.0.0",
                    "min_base_version": "2.0.0",
                    "max_base_version": "2.999.999",
                    "artifacts": {
                        "linux-x86_64": {
                            "url": "https://example.invalid/ai-2.0.0.zip",
                            "checksum": "sha256:0",
                            "size": 1024
                        },
                        "windows-x86_64": {
                            "url": "https://example.invalid/ai-2.0.0-win.zip",
                            "checksum": "sha256:0",
                            "size": 1024
                        }
                    }
                }
            ]
        }
    ]"#;

    #[test]
    fn parses_and_rejects_garbage() {
        let entries = parse_catalog(MANIFEST).expect("parses");
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].id, "com.channelflow.ai");
        assert_eq!(entries[0].image_url, "https://example.invalid/ai.png");

        assert!(parse_catalog("not json").is_err());
        assert!(parse_catalog("[]").is_err());
        assert!(parse_catalog(r#"[{"name":"no id"}]"#).is_err());
    }

    #[test]
    fn picks_the_requested_version_and_rejects_incompatible_ones() {
        let entries = parse_catalog(MANIFEST).expect("parses");
        let entry = &entries[0];

        let picked = pick_version(entry, "2.0.0", None).expect("latest compatible");
        assert_eq!(picked.version, "2.0.0");

        let picked = pick_version(entry, "2.0.0", Some("2.0.0")).expect("requested");
        assert_eq!(picked.version, "2.0.0");

        let old = pick_version(entry, "2.0.0", Some("1.5.0"));
        assert!(old.is_err(), "1.5.0 is outside the 2.0.0 base");

        let none_compatible = pick_version(entry, "3.0.0", None);
        assert!(none_compatible.is_err(), "nothing is compatible with base 3");
    }

    #[test]
    fn selects_an_artifact_for_the_host_rid() {
        let entries = parse_catalog(MANIFEST).expect("parses");
        let entry = &entries[0];
        let picked = pick_version(entry, "2.0.0", None).expect("pick");
        assert!(picked.artifacts.contains_key("linux-x86_64"));
        assert!(picked.artifacts.contains_key("windows-x86_64"));
        assert!(!picked.artifacts.contains_key("macos-aarch64"));
    }

    #[test]
    fn checksum_verification_matches_and_fails() {
        let dir = temp_dir("checksum");
        let file = dir.join("artifact.zip");
        std::fs::write(&file, b"payload").expect("write");
        let good = format!("sha256:{}", sha256_of(&file).expect("hash"));
        assert!(verify_checksum(&file, &good).is_ok());
        assert!(verify_checksum(&file, "sha256:0000000000000000000000000000000000000000000000000000000000000000").is_err());
        assert!(verify_checksum(&file, "md5:abc").is_err());
        assert!(verify_checksum(&file, "").is_ok(), "empty checksum means skip");
        drop(dir);
    }

    #[test]
    fn extracts_a_zip_and_rejects_escaping_entries() {
        let dir = temp_dir("extract");
        let zip_path = dir.join("plugin.zip");
        make_test_zip(&zip_path, false).expect("zip");
        let out = dir.join("out");
        extract_zip(&zip_path, &out).expect("extract");

        let manifest = std::fs::read_to_string(out.join("plugin.json")).expect("manifest");
        let parsed = PluginManifest::parse(&manifest).expect("valid manifest");
        assert_eq!(parsed.id, "com.channelflow.ai");
        drop(dir);
    }

    #[test]
    fn repository_records_round_trip_through_the_store() {
        let dir = temp_dir("repos");
        let store = Store::open(&dir).expect("store");
        let rt = tokio::runtime::Runtime::new().expect("rt");
        rt.block_on(async {
            assert!(store.repo_list().await.expect("empty").is_empty());
            let repos = store.repo_add("https://example.invalid/manifest.json").await.expect("add");
            assert_eq!(repos.len(), 1);
            assert!(store.repo_add("https://example.invalid/manifest.json").await.is_err(), "duplicate refused");
            let after = store.repo_list().await.expect("list");
            assert_eq!(after.len(), 1);
            let id = after[0]["id"].as_str().expect("id").to_string();
            let removed = store.repo_remove(&id).await.expect("remove");
            assert!(removed.is_empty());
            assert!(store.repo_remove(&id).await.is_err(), "missing id errors");
        });
        drop(dir);
    }

    fn temp_dir(label: &str) -> std::path::PathBuf {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("channelflow-repo-{label}-{stamp}"));
        std::fs::create_dir_all(&dir).expect("create temp dir");
        dir
    }

    /// Build a tiny but valid plugin zip with the `zip` crate's writer: a
    /// plugin.json and one library file.
    fn make_test_zip(path: &std::path::Path, escape: bool) -> std::io::Result<()> {
        let manifest = r#"{
            "id": "com.channelflow.ai",
            "name": "AI Provider Suite",
            "version": "2.0.0",
            "min_base_version": "2.0.0",
            "max_base_version": "2.999.999",
            "permissions": ["storage:read"],
            "entrypoint": { "linux-x86_64": "libchannelflow_plugin_ai.so" }
        }"#;
        let file = std::fs::File::create(path)?;
        let mut writer = zip::ZipWriter::new(file);
        writer.start_file(
            if escape { "../sneaky.txt" } else { "plugin.json" },
            zip::write::SimpleFileOptions::default(),
        )?;
        writer.write_all(manifest.as_bytes())?;
        if !escape {
            writer.start_file("libchannelflow_plugin_ai.so", zip::write::SimpleFileOptions::default())?;
            writer.write_all(b"\x7fELF fake library")?;
        }
        writer.finish()?;
        Ok(())
    }
}