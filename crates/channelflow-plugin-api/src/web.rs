//! Static web assets a plugin ships with itself.
//!
//! A plugin that adds its own UI ships the files in its own folder (the
//! plugin's `web/` directory) and publishes them here during `on_load`. The
//! core serves them at `/plugin/{id}/web/{path}`, so `<config>/webui` stays
//! the base system's tree — anything a plugin adds to the web UI is hosted
//! from the plugin's own folder.

use std::sync::{Arc, Mutex};

/// One static file a plugin published for the shell to serve.
#[derive(Debug, Clone)]
pub struct WebAsset {
    /// The path under `/plugin/{id}/web/`, e.g. `ai.css` (no leading slash).
    pub path: String,
    /// The HTTP content type, e.g. `text/css`.
    pub mime: String,
    /// The file's bytes.
    pub bytes: Vec<u8>,
}

/// The registry a plugin hands its web files to during `on_load`. The core
/// keeps the same handle, so after load it can look files up by path and
/// serve them.
#[derive(Clone, Default)]
pub struct PluginWeb {
    assets: Arc<Mutex<Vec<WebAsset>>>,
}

impl PluginWeb {
    /// An empty registry.
    pub fn new() -> Self {
        Self::default()
    }

    /// Publish one static file to `/plugin/{id}/web/{path}`. Registering the
    /// same path again replaces the earlier bytes.
    pub fn serve(&self, path: impl Into<String>, mime: impl Into<String>, bytes: Vec<u8>) {
        let asset = WebAsset {
            path: path.into(),
            mime: mime.into(),
            bytes,
        };
        if let Ok(mut assets) = self.assets.lock() {
            assets.retain(|existing| existing.path != asset.path);
            assets.push(asset);
        }
    }

    /// Convenience for compile-time-embedded files.
    pub fn serve_embedded(&self, path: &str, mime: &str, bytes: &'static [u8]) {
        self.serve(path, mime, bytes.to_vec());
    }

    /// The file at `path`, if this plugin published one.
    pub fn asset(&self, path: &str) -> Option<WebAsset> {
        self.assets
            .lock()
            .ok()
            .and_then(|assets| assets.iter().find(|asset| asset.path == path).cloned())
    }

    /// How many files this plugin published (used by tests and debugging).
    pub fn len(&self) -> usize {
        self.assets.lock().map(|assets| assets.len()).unwrap_or(0)
    }
}