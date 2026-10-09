//! The [`Plugin`] trait and the API a loaded plugin is handed.
//!
//! A plugin implements `Plugin` and returns itself from its ABI entrypoint
//! once dynamic loading lands. Today the bundled plugins are constructed
//! directly by the base, but they exercise the exact same interface.

use std::path::PathBuf;
use std::sync::Arc;

use async_trait::async_trait;
use axum::Router;

use crate::core::CoreData;
use crate::database::PluginDatabase;
use crate::manifest::PluginManifest;
use crate::storage::PluginStorage;
use crate::ui::UiContribution;

/// Plugin ABI version. The core refuses to load a plugin built against a
/// different one, so breaking changes to this contract cannot silently break
/// running plugins.
pub const PLUGIN_ABI_VERSION: u32 = 1;

/// Something a plugin did that the base should surface (and, at load time,
/// refuse to run the plugin over).
#[derive(Debug)]
pub struct PluginError(pub String);

impl std::fmt::Display for PluginError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for PluginError {}

impl PluginError {
    pub fn new(message: impl Into<String>) -> Self {
        PluginError(message.into())
    }
}

/// A plugin's answer when the base asks how it is doing.
#[derive(Debug, Clone, serde::Serialize)]
pub struct PluginHealth {
    pub ok: bool,
    pub detail: String,
}

/// A logger scoped to one plugin. `tracing` is the sink; the target carries
/// the plugin's id so its lines are filterable and attributable.
#[derive(Debug, Clone)]
pub struct PluginLogger {
    target: String,
}

impl PluginLogger {
    pub fn new(plugin_id: &str) -> Self {
        Self {
            target: format!("channelflow.plugin.{plugin_id}"),
        }
    }

    pub fn info(&self, message: &str) {
        tracing::info!(plugin = %self.target, "{message}");
    }

    pub fn warn(&self, message: &str) {
        tracing::warn!(plugin = %self.target, "{message}");
    }

    pub fn error(&self, message: &str) {
        tracing::error!(plugin = %self.target, "{message}");
    }
}

/// Everything the core gives a plugin when it loads.
#[derive(Clone)]
pub struct PluginApi {
    /// This plugin's manifest id, also its storage namespace and route mount.
    pub id: String,
    /// Key/value storage scoped strictly to this plugin.
    pub storage: Arc<dyn PluginStorage>,
    /// A shared outbound HTTP client (only useful with `network:outbound`).
    pub http: reqwest::Client,
    /// The base version this core is, for plugins that want to branch.
    pub base_version: String,
    /// Where the plugin's own files live: `<config>/plugins/{id}`.
    pub dir: PathBuf,
    pub logger: PluginLogger,
    /// Read-only view of core data (only useful with `api:core:read`).
    pub core: Arc<dyn CoreData>,
    /// The plugin's own Postgres tables (only useful with `storage:database`).
    pub database: Arc<dyn PluginDatabase>,
}

impl std::fmt::Debug for PluginApi {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PluginApi")
            .field("id", &self.id)
            .field("base_version", &self.base_version)
            .field("dir", &self.dir)
            .finish_non_exhaustive()
    }
}

/// The contract every plugin implements. Lifecycle calls that may do work are
/// async because loading a plugin can mean reading its own storage or hitting
/// the network to warm a pool.
#[async_trait]
pub trait Plugin: Send + Sync {
    /// The plugin's `plugin.json`, parsed.
    fn metadata(&self) -> &PluginManifest;

    /// Called once, right after the plugin is loaded, before it is enabled.
    /// The plugin keeps whatever it needs from `PluginApi` (typically the
    /// storage handle) and is expected to return quickly.
    async fn on_load(&mut self, api: PluginApi) -> Result<(), PluginError>;

    /// Called when the plugin is enabled. This is where it starts working.
    async fn on_enable(&mut self) -> Result<(), PluginError>;

    /// Called when the plugin is disabled; it should stop working.
    async fn on_disable(&mut self) -> Result<(), PluginError>;

    /// Called before the plugin is unloaded, for cleanup. Once called, a
    /// plugin is not expected to run again.
    fn on_unload(&mut self);

    /// Handle a configuration change delivered by the core.
    async fn on_config(&mut self, config: serde_json::Value) -> Result<(), PluginError>;

    /// API routes to mount under `/api/plugins/{id}`.
    fn routes(&self) -> Option<Router>;

    /// UI contributions this plugin declares (the shell's loader interprets
    /// them; the manager serves them).
    fn ui_contributions(&self) -> Vec<UiContribution>;

    /// The plugin's current health.
    fn health(&self) -> PluginHealth;
}

/// A plugin whose methods are all no-ops, for layering implementations on top
/// of. Bundled plugins override the calls they care about.
pub struct NoopPlugin {
    pub metadata: PluginManifest,
}

impl NoopPlugin {
    pub fn new(metadata: PluginManifest) -> Self {
        Self { metadata }
    }
}

#[async_trait]
impl Plugin for NoopPlugin {
    fn metadata(&self) -> &PluginManifest {
        &self.metadata
    }

    async fn on_load(&mut self, _api: PluginApi) -> Result<(), PluginError> {
        Ok(())
    }

    async fn on_enable(&mut self) -> Result<(), PluginError> {
        Ok(())
    }

    async fn on_disable(&mut self) -> Result<(), PluginError> {
        Ok(())
    }

    fn on_unload(&mut self) {}

    async fn on_config(&mut self, _config: serde_json::Value) -> Result<(), PluginError> {
        Ok(())
    }

    fn routes(&self) -> Option<Router> {
        None
    }

    fn ui_contributions(&self) -> Vec<UiContribution> {
        Vec::new()
    }

    fn health(&self) -> PluginHealth {
        PluginHealth {
            ok: true,
            detail: "idle".to_string(),
        }
    }
}