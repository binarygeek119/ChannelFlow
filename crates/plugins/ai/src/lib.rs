//! The AI Provider Suite plugin.
//!
//! Everything that was single-purpose in the 2.0.0 base is extracted here
//! behind the [`Plugin`] trait: the provider list model, the test probes, and
//! the API the AI page calls. The provider list is persisted through the
//! plugin's own namespaced storage, keyed as `providers`.
//!
//! The AI page stays part of the base's app shell but talks only to routes
//! this plugin mounts under `/api/plugins/com.channelflow.ai`.

mod ai;
mod api;
mod openai;

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;

use axum::Router;
use channelflow_plugin_api::{
    manifest::PluginManifest,
    plugin::{Plugin, PluginApi, PluginError, PluginHealth, PluginLogger},
    PluginStorage, UiContribution,
};
use tokio::sync::Mutex;

pub use ai::{AiConfig, AiError, AiView, ProviderView};

/// The key the provider list is stored under in the plugin's namespaced
/// storage.
pub const PROVIDERS_KEY: &str = "providers";

/// The shipped `plugin.json` is the single source of truth for the manifest
/// the plugin reports to the manager.
fn manifest() -> PluginManifest {
    PluginManifest::parse(include_str!("../plugin.json")).expect("bundled plugin.json is valid")
}

/// The one thing the base needs to construct this plugin.
pub fn plugin() -> Box<dyn Plugin> {
    Box::new(AiPlugin::new())
}

pub struct AiPlugin {
    metadata: PluginManifest,
    state: Option<Arc<AiState>>,
    enabled: bool,
}

/// Shared runtime handed to the route handlers: the current provider list and
/// the handles that came out of `PluginApi`.
pub struct AiState {
    pub config: Mutex<AiConfig>,
    pub storage: Arc<dyn PluginStorage>,
    pub http: reqwest::Client,
    pub logger: PluginLogger,
    /// Provider count, kept lock-free so `health()` stays sync.
    providers: AtomicUsize,
}

impl AiPlugin {
    pub fn new() -> Self {
        Self {
            metadata: manifest(),
            state: None,
            enabled: false,
        }
    }

    fn state(&self) -> Result<Arc<AiState>, PluginError> {
        self.state
            .clone()
            .ok_or_else(|| PluginError::new("AI plugin has not been loaded"))
    }
}

impl AiState {
    /// Replace the provider list and write it to storage.
    async fn store(&self, config: &AiConfig) -> Result<(), PluginError> {
        let value = serde_json::to_value(config)
            .map_err(|error| PluginError::new(error.to_string()))?;
        self.storage
            .set(PROVIDERS_KEY, &value)
            .await
            .map_err(|error| PluginError::new(error.to_string()))?;
        *self.config.lock().await = config.clone();
        self.providers
            .store(config.providers.len(), Ordering::Relaxed);
        Ok(())
    }

    async fn lock(&self) -> AiConfig {
        self.config.lock().await.clone()
    }
}

#[async_trait::async_trait]
impl Plugin for AiPlugin {
    fn metadata(&self) -> &PluginManifest {
        &self.metadata
    }

    async fn on_load(&mut self, api: PluginApi) -> Result<(), PluginError> {
        let config = match api
            .storage
            .get(PROVIDERS_KEY)
            .await
            .map_err(|error| PluginError::new(error.to_string()))?
        {
            Some(value) => AiConfig::parse(&value)
                .map_err(|error| PluginError::new(error.to_string()))?,
            None => AiConfig::default(),
        };
        let provider_count = config.providers.len();
        self.state = Some(Arc::new(AiState {
            providers: AtomicUsize::new(provider_count),
            config: Mutex::new(config),
            storage: api.storage,
            http: api.http,
            logger: api.logger.clone(),
        }));
        api.logger.info("loaded");
        Ok(())
    }

    async fn on_enable(&mut self) -> Result<(), PluginError> {
        if self.enabled {
            return Ok(());
        }
        self.enabled = true;
        if let Some(state) = &self.state {
            state
                .logger
                .info(&format!("enabled with {} provider(s)", state.providers.load(Ordering::Relaxed)));
        }
        Ok(())
    }

    async fn on_disable(&mut self) -> Result<(), PluginError> {
        if !self.enabled {
            return Ok(());
        }
        self.enabled = false;
        if let Some(state) = &self.state {
            state.logger.info("disabled");
        }
        Ok(())
    }

    fn on_unload(&mut self) {
        self.state = None;
    }

    async fn on_config(&mut self, config: serde_json::Value) -> Result<(), PluginError> {
        let state = self.state()?;
        let parsed =
            AiConfig::parse(&config).map_err(|error| PluginError::new(error.to_string()))?;
        state.store(&parsed).await?;
        Ok(())
    }

    fn routes(&self) -> Option<Router> {
        self.state().ok().map(api::router)
    }

    fn ui_contributions(&self) -> Vec<UiContribution> {
        self.metadata.ui_contributions.clone()
    }

    fn health(&self) -> PluginHealth {
        let providers = self
            .state
            .as_ref()
            .map(|state| state.providers.load(Ordering::Relaxed))
            .unwrap_or(0);
        PluginHealth {
            ok: self.enabled,
            detail: format!(
                "{} with {providers} provider(s)",
                if self.enabled { "enabled" } else { "disabled" }
            ),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use channelflow_plugin_api::storage::InMemoryStorage;

    /// The shipped plugin.json parses to the manifest the plugin reports, and
    /// is compatible with this base.
    #[test]
    fn bundled_manifest_is_valid_and_compatible() {
        let plugin = AiPlugin::new();
        let metadata = plugin.metadata();
        assert_eq!(metadata.id, "com.channelflow.ai");
        assert!(metadata.compatible_with("2.0.0"), "must run on this base");
        assert!(!metadata.permissions.is_empty());
    }

    #[tokio::test]
    async fn loads_its_provider_list_from_storage() {
        let mut storage = InMemoryStorage::new();
        storage.seed(
            PROVIDERS_KEY,
            serde_json::json!({ "providers": [{ "id": "p1", "name": "One", "priority": 1 }] }),
        );

        let mut plugin = AiPlugin::new();
        let api = PluginApi {
            id: metadata_id(),
            storage: Arc::new(storage),
            http: reqwest::Client::new(),
            base_version: "2.0.0".to_string(),
            dir: std::env::temp_dir(),
            logger: PluginLogger::new("com.channelflow.ai"),
        };
        plugin.on_load(api).await.expect("load");
        assert_eq!(plugin.health().detail, "disabled with 1 provider(s)");
        assert!(!plugin.enabled);
        plugin.on_enable().await.expect("enable");
        assert!(plugin.health().ok);
    }

    fn metadata_id() -> String {
        "com.channelflow.ai".to_string()
    }
}