//! Reads the plugin repository's `manifest.json` — the Jellyfin-style index
//! the `ChannelFlow-Plugins` repo publishes. The result is cached briefly so
//! opening the Store a few times does not hammer GitHub.

use std::collections::BTreeMap;
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use tokio::sync::Mutex;

/// How long a fetched manifest is reused before it is fetched again.
const CACHE_TTL: Duration = Duration::from_secs(300);

/// One downloadable build of a plugin.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StoreArtifact {
    pub url: String,
    #[serde(default)]
    pub checksum: String,
    #[serde(default)]
    pub size: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StoreVersion {
    pub version: String,
    #[serde(default)]
    pub min_base_version: String,
    #[serde(default)]
    pub max_base_version: String,
    #[serde(default)]
    pub timestamp: String,
    #[serde(default)]
    pub artifacts: BTreeMap<String, StoreArtifact>,
}

/// One plugin as the repository describes it.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StorePlugin {
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub description: String,
    #[serde(default)]
    pub owner: String,
    #[serde(default)]
    pub category: String,
    #[serde(default)]
    pub homepage: String,
    #[serde(default)]
    pub versions: Vec<StoreVersion>,
    #[serde(rename = "imageUrl", default)]
    pub image_url: String,
}

impl StorePlugin {
    /// The newest version the repository offers, if it lists any.
    pub fn latest(&self) -> Option<&StoreVersion> {
        self.versions.first()
    }
}

/// The repository, fetched over HTTP and cached in memory.
pub struct StoreClient {
    http: reqwest::Client,
    url: String,
    cache: Mutex<Option<(Instant, Arc<Vec<StorePlugin>>)>>,
}

impl StoreClient {
    pub fn new(http: reqwest::Client, url: String) -> Self {
        Self {
            http,
            url,
            cache: Mutex::new(None),
        }
    }

    pub fn url(&self) -> &str {
        &self.url
    }

    /// Fetch the manifest, or return the cached copy if it is still fresh.
    pub async fn fetch(&self) -> Result<Arc<Vec<StorePlugin>>, String> {
        let mut cache = self.cache.lock().await;
        if let Some((at, plugins)) = cache.as_ref() {
            if at.elapsed() < CACHE_TTL {
                return Ok(plugins.clone());
            }
        }
        let response = self
            .http
            .get(&self.url)
            .send()
            .await
            .map_err(|error| format!("could not reach {url}: {error}", url = self.url))?;
        let status = response.status();
        let body = response.text().await.unwrap_or_default();
        if !status.is_success() {
            return Err(format!("the plugin store answered HTTP {}", status.as_u16()));
        }
        let plugins: Vec<StorePlugin> = serde_json::from_str(&body)
            .map_err(|error| format!("the plugin store manifest is not valid JSON: {error}"))?;
        let plugins = Arc::new(plugins);
        *cache = Some((Instant::now(), plugins.clone()));
        Ok(plugins)
    }
}