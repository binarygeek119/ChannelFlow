//! Inter-plugin contributions: places one plugin can hand something to
//! another.
//!
//! Every plugin in a build lives in the same process and links the same SDK
//! crate, so a small process-global registry lets one plugin offer work to
//! another — for example the Holiday Channel contributing itself as a preset
//! to the Presets page. A contributed preset is registered on the plugin's
//! `on_load` (and dropped on `on_unload`), so it appears while the plugin is
//! installed and no more.

use std::sync::{Mutex, OnceLock};

use serde::Serialize;

/// A ready-made channel another plugin contributes to the Presets page.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct PresetContribution {
    /// The stable preset/channel id, e.g. `channelflow-holiday`.
    pub id: String,
    /// The plugin that owns this preset (its manifest id). The Presets page
    /// shows the contribution only while that plugin is installed.
    pub plugin_id: String,
    /// The v1.0.0 original dial number ("retro"): 207 for the Holiday Channel.
    pub legacy: f64,
    /// The v1.0.0 subchannel dial number ("modern"): 203.4.
    pub subchannel: f64,
    pub name: String,
    pub category: String,
    pub description: String,
    /// A served logo URL for this channel's watermark, owned by this plugin
    /// (e.g. `/plugin/com.channelflow.news/web/logos/…`). Empty falls back to
    /// the Presets page's own artwork.
    #[serde(default)]
    pub logo: String,
}

static PRESETS: OnceLock<Mutex<Vec<PresetContribution>>> = OnceLock::new();

/// Register (or replace by id) a preset this plugin contributes.
pub fn register_preset(preset: PresetContribution) {
    let Ok(mut registry) = PRESETS
        .get_or_init(|| Mutex::new(Vec::new()))
        .lock() else {
        return;
    };
    registry.retain(|existing| existing.id != preset.id);
    registry.push(preset);
}

/// Drop a contributed preset, because its plugin was unloaded or uninstalled.
pub fn unregister_preset(id: &str) {
    if let Some(registry) = PRESETS.get() {
        if let Ok(mut registry) = registry.lock() {
            registry.retain(|preset| preset.id != id);
        }
    }
}

/// The presets other plugins currently contribute, in registration order.
pub fn contributed_presets() -> Vec<PresetContribution> {
    PRESETS
        .get()
        .map(|registry| registry.lock().map(|registry| registry.clone()).unwrap_or_default())
        .unwrap_or_default()
}