//! The media-source plugins registered at startup.
//!
//! Plugins that implement the SDK's [`MediaSource`] contract register here
//! alongside their ordinary [`Plugin`] lifecycle. The registry is what the
//! connection forms, the catalog, and (soon) the sync driver talk to; nothing
//! here knows a specific source (Jellyfin, …) by name.

use channelflow_plugin_api::media::MediaSource;

/// The registered media sources, in registration order.
///
/// Each source knows which plugin id registered it, so the connection tab can
/// offer a type only when its plugin is actually installed — registering an
/// Emby or Plex plugin later is what makes "emby"/"plex" appear; a
/// compiled-in "local" source that has no installed plugin stays hidden.
pub struct MediaSources {
    sources: Vec<RegisteredSource>,
}

struct RegisteredSource {
    /// The plugin id that provides this source, e.g. `com.channelflow.jellyfin`.
    plugin_id: String,
    source: Box<dyn MediaSource>,
}

impl MediaSources {
    pub fn new() -> Self {
        Self {
            sources: Vec::new(),
        }
    }

    /// Register one source on behalf of a plugin. Later registrations of the
    /// same `type_id` replace the earlier one.
    pub fn register(&mut self, plugin_id: &str, source: Box<dyn MediaSource>) {
        let type_id = source.type_id().to_string();
        self.sources
            .retain(|entry| entry.source.type_id() != type_id);
        self.sources.push(RegisteredSource {
            plugin_id: plugin_id.to_string(),
            source,
        });
    }

    /// One source by `type_id` (the connection `kind`), if registered. Unlike
    /// [`Self::find_installed`] this ignores installation state — kept for
    /// tests and flows that must see a source even when its plugin is gone.
    #[allow(dead_code)]
    pub fn find(&self, type_id: &str) -> Option<&dyn MediaSource> {
        self.sources
            .iter()
            .map(|entry| entry.source.as_ref())
            .find(|source| source.type_id() == type_id)
    }

    /// One source by `type_id`, but only when its owning plugin is installed
    /// (`installed(plugin_id)` is true). Invalid kinds, and kinds whose plugin
    /// was uninstalled, both come back `None`.
    pub fn find_installed(
        &self,
        type_id: &str,
        installed: &dyn Fn(&str) -> bool,
    ) -> Option<&dyn MediaSource> {
        self.sources
            .iter()
            .find(|entry| entry.source.type_id() == type_id && installed(&entry.plugin_id))
            .map(|entry| entry.source.as_ref())
    }

    /// The catalog the connection form renders — only sources whose plugin is
    /// installed — with their identity, connection fields, and supported
    /// media.
    pub fn catalog(&self, installed: &dyn Fn(&str) -> bool) -> Vec<serde_json::Value> {
        self.sources
            .iter()
            .filter(|entry| installed(&entry.plugin_id))
            .map(|entry| {
                let source = entry.source.as_ref();
                serde_json::json!({
                    "plugin_id": entry.plugin_id,
                    "type_id": source.type_id(),
                    "display_name": source.display_name(),
                    "fields": source.connection_fields(),
                    "supported_media": source.supported_media(),
                })
            })
            .collect()
    }
}

impl Default for MediaSources {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use channelflow_plugin_api::media::{
        Connection, FieldSpec, Library, MediaType, SyncCtx, SyncReport, TestResult,
    };
    use async_trait::async_trait;

    struct StubSource;

    #[async_trait]
    impl MediaSource for StubSource {
        fn type_id(&self) -> &'static str {
            "stub"
        }
        fn display_name(&self) -> &'static str {
            "Stub"
        }
        fn connection_fields(&self) -> Vec<FieldSpec> {
            vec![FieldSpec::text("url", "Server URL").required()]
        }
        fn supported_media(&self) -> &[MediaType] {
            &[MediaType::Movie]
        }
        async fn test_connection(&self, _connection: &Connection, _key: &str) -> TestResult {
            TestResult::ok("fine")
        }
        async fn list_libraries(&self, _connection: &Connection, _key: &str) -> Vec<Library> {
            Vec::new()
        }
        async fn sync_library(&self, _ctx: SyncCtx) -> SyncReport {
            SyncReport::default()
        }
    }

    #[test]
    fn registers_by_type_id_and_survives_re_registration() {
        let mut sources = MediaSources::new();
        let installed = |_id: &str| true;
        sources.register("stub.plugin", Box::new(StubSource));
        sources.register("stub.plugin", Box::new(StubSource));
        assert_eq!(sources.catalog(&installed).len(), 1);
        assert!(sources.find("stub").is_some());
        assert!(sources.find_installed("stub", &installed).is_some());
        assert!(sources.find("nope").is_none());
        let catalog = sources.catalog(&installed);
        assert_eq!(catalog[0]["type_id"], "stub");
        assert_eq!(catalog[0]["plugin_id"], "stub.plugin");
    }

    #[test]
    fn an_uninstalled_plugins_source_is_hidden() {
        let mut sources = MediaSources::new();
        sources.register("stub.plugin", Box::new(StubSource));
        let installed = |id: &str| id != "stub.plugin";
        assert!(sources.catalog(&installed).is_empty());
        assert!(sources.find_installed("stub", &installed).is_none());
        // Registered but invisible: the create-connection path refuses it too.
        assert!(sources.find("stub").is_some());
    }

    #[test]
    fn shapes_stay_stable() {
        let mut sources = MediaSources::new();
        sources.register("stub.plugin", Box::new(StubSource));
        let catalog = sources.catalog(&|_id: &str| true);
        let field = &catalog[0]["fields"][0];
        assert_eq!(field["kind"], "text");
        assert_eq!(catalog[0]["supported_media"][0], "movie");
    }
}