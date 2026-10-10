//! The registry and lifecycle for loaded plugins.
//!
//! Plugins are added at startup with their [`PluginApi`] already built; the
//! manager calls `on_load` immediately and `on_enable` when told to. Routes
//! each plugin offers are collected once and mounted by the core under
//! `/api/plugins/{id}`. The same manager will drive a dynamic loader later —
//! nothing here knows or cares where the `Box<dyn Plugin>` came from.

use axum::Router;
use channelflow_plugin_api::manifest::PluginManifest;
use channelflow_plugin_api::{Plugin, PluginApi, PluginError, PluginWeb, WebAsset};

/// One loaded plugin plus the state around its lifecycle.
pub struct ManagedPlugin {
    pub plugin: Box<dyn Plugin>,
    pub enabled: bool,
    /// Whether the plugin's version range covers this base. An incompatible
    /// plugin is listed (so it can be seen and updated) but can never run.
    pub compatible: bool,
    /// The web files the plugin published during `on_load`; the core serves
    /// them at `/plugin/{id}/web/{path}`.
    pub web: PluginWeb,
}

impl ManagedPlugin {
    pub fn id(&self) -> &str {
        &self.plugin.metadata().id
    }

    pub fn manifest(&self) -> &PluginManifest {
        self.plugin.metadata()
    }

    /// The catalog entry served by `GET /api/plugins`.
    pub fn catalog_entry(&self) -> serde_json::Value {
        let manifest = self.manifest();
        serde_json::json!({
            "id": manifest.id,
            "name": manifest.name,
            "version": manifest.version,
            "category": manifest.category,
            "description": manifest.description,
            "author": manifest.author,
            "homepage": manifest.homepage,
            "repository": manifest.repository,
            "compatible": self.compatible,
            "enabled": self.enabled,
            "permissions": manifest.permissions,
            "ui_contributions": manifest.ui_contributions,
            "health": self.plugin.health(),
        })
    }
}

/// The set of loaded plugins, managed in plugin-id order.
pub struct PluginManager {
    base_version: String,
    plugins: Vec<ManagedPlugin>,
}

impl PluginManager {
    pub fn new(base_version: &str) -> Self {
        Self {
            base_version: base_version.to_string(),
            plugins: Vec::new(),
        }
    }

    /// Call `on_unload` and drop the plugin from the registry. The dynamic
    /// loader will use this when a plugin is uninstalled; compiled-in plugins
    /// live for the process.
    #[allow(dead_code)]
    pub fn unload(&mut self, id: &str) {
        if let Some(index) = self.plugins.iter().position(|plugin| plugin.id() == id) {
            let mut plugin = self.plugins.remove(index);
            plugin.plugin.on_unload();
        }
    }

    /// Register a plugin: hand it its `PluginApi`, call `on_load`, and hold it
    /// ready to be enabled. Incompatibility is recorded, not fatal here — the
    /// plugin shows up in the catalog with `compatible: false`.
    pub async fn add(&mut self, mut plugin: Box<dyn Plugin>, api: PluginApi) -> Result<(), String> {
        // The plugin fills `web` during on_load; the manager keeps the handle
        // so it can serve what the plugin published.
        let web = api.web.clone();
        let compatible = plugin.metadata().compatible_with(&self.base_version);
        plugin
            .on_load(api)
            .await
            .map_err(|error| format!("{} failed to load: {error}", plugin.metadata().id))?;
        self.plugins.push(ManagedPlugin {
            plugin,
            enabled: false,
            compatible,
            web,
        });
        self.plugins.sort_by(|a, b| a.id().cmp(b.id()));
        Ok(())
    }

    /// Call `on_enable` and mark the plugin enabled.
    pub async fn enable(&mut self, id: &str) -> Result<(), PluginError> {
        let base_version = self.base_version.clone();
        let plugin = self
            .find_mut(id)
            .ok_or_else(|| PluginError::new(format!("no plugin with id {id}")))?;
        if !plugin.compatible {
            return Err(PluginError::new(format!(
                "{id} needs a {}-compatible base, this is {base_version}",
                plugin.manifest().min_base_version
            )));
        }
        plugin.plugin.on_enable().await?;
        plugin.enabled = true;
        Ok(())
    }

    /// Call `on_disable` and mark the plugin disabled.
    pub async fn disable(&mut self, id: &str) -> Result<(), PluginError> {
        let plugin = self
            .find_mut(id)
            .ok_or_else(|| PluginError::new(format!("no plugin with id {id}")))?;
        plugin.plugin.on_disable().await?;
        plugin.enabled = false;
        Ok(())
    }

    /// The routers each enabled plugin offers, keyed by plugin id, ready to be
    /// nested under `/api/plugins/{id}`.
    pub fn routers(&self) -> Vec<(String, Router)> {
        self.plugins
            .iter()
            .filter(|plugin| plugin.plugin.routes().is_some())
            .map(|plugin| {
                (
                    plugin.id().to_string(),
                    plugin.plugin.routes().expect("checked just above"),
                )
            })
            .collect()
    }

    /// The catalog the Plugins page renders.
    pub fn catalog(&self) -> Vec<serde_json::Value> {
        self.plugins
            .iter()
            .map(ManagedPlugin::catalog_entry)
            .collect()
    }

    /// A web file a plugin published, keyed by plugin id and path.
    pub fn web_asset(&self, id: &str, path: &str) -> Option<WebAsset> {
        self.plugins
            .iter()
            .find(|plugin| plugin.id() == id)
            .and_then(|plugin| plugin.web.asset(path))
    }

    fn find_mut(&mut self, id: &str) -> Option<&mut ManagedPlugin> {
        self.plugins.iter_mut().find(|plugin| plugin.id() == id)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use channelflow_plugin_api::manifest::PluginManifest;
    use channelflow_plugin_api::storage::InMemoryStorage;
    use channelflow_plugin_api::{NoopPlugin, PluginLogger};

    fn api(id: &str) -> PluginApi {
        PluginApi {
            id: id.to_string(),
            storage: std::sync::Arc::new(InMemoryStorage::new()),
            http: reqwest::Client::new(),
            base_version: "2.0.0".to_string(),
            dir: std::env::temp_dir(),
            logger: PluginLogger::new(id),
            core: std::sync::Arc::new(channelflow_plugin_api::core::NoCoreData::default()),
            database: std::sync::Arc::new(channelflow_plugin_api::database::NoPluginDatabase::default()),
            web: PluginWeb::new(),
        }
    }

    fn manifest(id: &str) -> PluginManifest {
        PluginManifest::parse(&format!(
            r#"{{ "id": "{id}", "name": "{id}", "version": "1.0.0", "min_base_version": "2.0.0", "max_base_version": "2.999.999" }}"#
        ))
        .expect("manifest")
    }

    #[tokio::test]
    async fn lifecycle_runs_in_order() {
        let mut manager = PluginManager::new("2.0.0");
        manager.add(Box::new(NoopPlugin::new(manifest("a"))), api("a")).await.unwrap();
        manager.enable("a").await.unwrap();
        assert!(manager.catalog()[0]["enabled"].as_bool().unwrap());
        manager.disable("a").await.unwrap();
        assert!(!manager.catalog()[0]["enabled"].as_bool().unwrap());
    }

    #[tokio::test]
    async fn incompatible_plugins_are_listed_but_cannot_enable() {
        let mut manager = PluginManager::new("2.0.0");
        let too_new = manifest("new");
        let mut too_new = too_new;
        too_new.min_base_version = "3.0.0".to_string();
        manager.add(Box::new(NoopPlugin::new(too_new)), api("new")).await.unwrap();
        let entry = &manager.catalog()[0];
        assert!(!entry["compatible"].as_bool().unwrap());
        assert!(manager.enable("new").await.is_err());
    }

    #[tokio::test]
    async fn enable_disables_are_idempotent_and_missing_ids_error() {
        let mut manager = PluginManager::new("2.0.0");
        manager.add(Box::new(NoopPlugin::new(manifest("a"))), api("a")).await.unwrap();
        manager.enable("a").await.unwrap();
        manager.enable("a").await.unwrap();
        manager.disable("a").await.unwrap();
        manager.disable("a").await.unwrap();
        assert!(manager.enable("nope").await.is_err());
    }
}