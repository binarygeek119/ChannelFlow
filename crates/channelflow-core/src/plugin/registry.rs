//! Which plugins are installed, and whether each is enabled.
//!
//! Persisted as one core document (the `@core` namespace of the key/value
//! storage) so it rides on files or Postgres with everything else. The base's
//! bundled plugins are installed and enabled on first run; after that this
//! registry is the source of truth and removing a plugin is a real removal,
//! not just a restart away from coming back.

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct InstalledPlugin {
    pub id: String,
    /// The version that is installed — the build's version for compiled-in
    /// plugins.
    pub version: String,
    pub enabled: bool,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct PluginRegistry {
    #[serde(default)]
    pub installed: Vec<InstalledPlugin>,
}

impl PluginRegistry {
    pub fn get(&self, id: &str) -> Option<&InstalledPlugin> {
        self.installed.iter().find(|plugin| plugin.id == id)
    }

    /// Install (or reinstall at a new version) and enable.
    pub fn install(&mut self, id: &str, version: &str) {
        match self.installed.iter_mut().find(|plugin| plugin.id == id) {
            Some(existing) => {
                existing.version = version.to_string();
                existing.enabled = true;
            }
            None => self.installed.push(InstalledPlugin {
                id: id.to_string(),
                version: version.to_string(),
                enabled: true,
            }),
        }
    }

    /// Remove a plugin. Returns whether it was installed.
    pub fn remove(&mut self, id: &str) -> bool {
        let before = self.installed.len();
        self.installed.retain(|plugin| plugin.id != id);
        self.installed.len() != before
    }

    /// Enable or disable an installed plugin. Returns whether it was found.
    pub fn set_enabled(&mut self, id: &str, enabled: bool) -> bool {
        match self.installed.iter_mut().find(|plugin| plugin.id == id) {
            Some(existing) => {
                existing.enabled = enabled;
                true
            }
            None => false,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn install_enable_remove_round_trip() {
        let mut registry = PluginRegistry::default();
        registry.install("a", "1.0.0");
        assert!(registry.get("a").unwrap().enabled);
        assert!(registry.set_enabled("a", false));
        assert!(!registry.get("a").unwrap().enabled);
        // Reinstalling offers the new version and enables again.
        registry.install("a", "1.1.0");
        assert_eq!(registry.get("a").unwrap().version, "1.1.0");
        assert!(registry.get("a").unwrap().enabled);
        assert!(registry.remove("a"));
        assert!(registry.get("a").is_none());
        assert!(!registry.remove("a"), "removing twice is a no-op");
    }
}