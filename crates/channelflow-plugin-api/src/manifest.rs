//! `plugin.json` — the machine-readable description of a plugin.
//!
//! Parsed and validated, then surfaced by the Plugin Manager as its catalog
//! entries. The manifest is where a plugin asks for its [`Permission`]s and
//! declares what it contributes to the API and the UI.

use serde::{Deserialize, Serialize};

use crate::permission::Permission;
use crate::ui::UiContribution;

/// A plugin author's identity and the source of its releases.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Repository {
    #[serde(rename = "type")]
    pub kind: String,
    pub url: String,
    pub releases_url: String,
}

/// Named files the plugin ships with itself.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct Assets {
    pub migrations: Option<String>,
    pub web_static: Option<String>,
    pub templates: Option<String>,
}

/// Lifecycle entry points. For compiled-in plugins these stay `None`; the
/// string form is what a dynamic loader would resolve against the plugin
/// binary's symbols.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct Hooks {
    pub on_install: Option<String>,
    pub on_uninstall: Option<String>,
    pub on_enable: Option<String>,
    pub on_disable: Option<String>,
    pub on_config_change: Option<String>,
}

/// The whole `plugin.json`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PluginManifest {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub version: String,
    /// Lowest base version this plugin will run on.
    #[serde(default)]
    pub min_base_version: String,
    /// Highest base version this plugin will run on.
    #[serde(default)]
    pub max_base_version: String,
    #[serde(default)]
    pub author: String,
    #[serde(default)]
    pub description: String,
    #[serde(default)]
    pub homepage: String,
    #[serde(default)]
    pub repository: Option<Repository>,
    /// e.g. `transcoding`, `ai`, `utility`.
    #[serde(default)]
    pub category: String,
    /// The permissions the plugin requests, e.g. `storage:read`.
    #[serde(default)]
    pub permissions: Vec<String>,
    /// Per-platform shared library names, for dynamic loading.
    #[serde(default)]
    pub entrypoint: std::collections::BTreeMap<String, String>,
    #[serde(default)]
    pub assets: Assets,
    #[serde(default)]
    pub hooks: Hooks,
    /// What the plugin adds to the web UI.
    #[serde(default)]
    pub ui_contributions: Vec<UiContribution>,
}

impl PluginManifest {
    /// Parse and validate a `plugin.json` body.
    pub fn parse(value: &str) -> Result<Self, String> {
        let manifest: PluginManifest = serde_json::from_str(value)
            .map_err(|error| format!("plugin.json is not valid: {error}"))?;
        manifest.validate()?;
        Ok(manifest)
    }

    /// The manifest has to be about something: an id and a version at minimum,
    /// and the version range has to make sense.
    pub fn validate(&self) -> Result<(), String> {
        if self.id.trim().is_empty() {
            return Err("plugin.json needs an id".to_string());
        }
        if crate::version::parse(&self.version).is_err() {
            return Err(format!("plugin.json version \"{}\" is not a version", self.version));
        }
        if !self.min_base_version.is_empty() && !self.max_base_version.is_empty() {
            let min = crate::version::parse(&self.min_base_version).map_err(|e| e.to_string())?;
            let max = crate::version::parse(&self.max_base_version).map_err(|e| e.to_string())?;
            if min > max {
                return Err(format!(
                    "min_base_version {} is above max_base_version {}",
                    self.min_base_version, self.max_base_version
                ));
            }
        }
        // Every requested permission has to be one the base can understand; an
        // unknown string means a plugin that no sandbox could approve.
        for raw in &self.permissions {
            Permission::parse(raw)
                .map_err(|error| format!("permission {raw}: {error}"))?;
        }
        Ok(())
    }

    /// True when this plugin will run on the given base version.
    pub fn compatible_with(&self, base: &str) -> bool {
        crate::version::compatible(base, &self.min_base_version, &self.max_base_version)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_the_spec_example() {
        let manifest = PluginManifest::parse(
            r#"{
                "id": "com.channelflow.ersatztv",
                "name": "ErsatzTV Transcoding Engine",
                "version": "0.2.0",
                "min_base_version": "2.0.0",
                "max_base_version": "2.999.999",
                "category": "transcoding",
                "permissions": ["storage:read", "network:outbound"],
                "ui_contributions": [
                    { "type": "page", "id": "ersatztv-settings", "path": "/settings/ersatztv", "title": "ErsatzTV", "icon": "settings", "section": "settings", "component": "ErsatzTVSettingsPage" }
                ]
            }"#,
        )
        .expect("manifest parses");
        assert_eq!(manifest.id, "com.channelflow.ersatztv");
        assert!(manifest.compatible_with("2.0.0"));
        assert!(!manifest.compatible_with("3.0.0"));
        assert_eq!(manifest.ui_contributions.len(), 1);
    }

    #[test]
    fn rejects_unknown_permissions_and_nonsense() {
        let bad = PluginManifest::parse(
            r#"{ "id": "x", "version": "1.0.0", "permissions": ["chmod:everything"] }"#,
        );
        assert!(bad.is_err(), "unknown permission must be rejected");

        let bad_range = PluginManifest::parse(
            r#"{ "id": "x", "version": "1.0.0", "min_base_version": "2.0.0", "max_base_version": "1.0.0" }"#,
        );
        assert!(bad_range.is_err());
    }
}