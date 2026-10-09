//! The media-source plugins registered at startup.
//!
//! Plugins that implement the SDK's [`MediaSource`] contract register here
//! alongside their ordinary [`Plugin`] lifecycle. The registry is what the
//! connection forms, the catalog, and (soon) the sync driver talk to; nothing
//! here knows a specific source (Jellyfin, …) by name.

use channelflow_plugin_api::media::MediaSource;

/// The registered media sources, in registration order.
pub struct MediaSources {
    sources: Vec<Box<dyn MediaSource>>,
}

impl MediaSources {
    pub fn new() -> Self {
        Self {
            sources: Vec::new(),
        }
    }

    /// Register one source. Later registrations of the same `type_id` replace
    /// the earlier one, so a source can be re-registered at runtime.
    pub fn register(&mut self, source: Box<dyn MediaSource>) {
        let type_id = source.type_id().to_string();
        self.sources.retain(|existing| existing.type_id() != type_id);
        self.sources.push(source);
    }

    /// One source by `type_id` (the connection `kind`), if registered.
    pub fn find(&self, type_id: &str) -> Option<&dyn MediaSource> {
        self.sources
            .iter()
            .map(|source| source.as_ref())
            .find(|source| source.type_id() == type_id)
    }

    /// The catalog the connection form renders: identity, the fields a
    /// connection needs, and what media it supports.
    pub fn catalog(&self) -> Vec<serde_json::Value> {
        self.sources
            .iter()
            .map(|source| {
                let source = source.as_ref();
                serde_json::json!({
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
        sources.register(Box::new(StubSource));
        sources.register(Box::new(StubSource));
        assert_eq!(sources.catalog().len(), 1);
        assert!(sources.find("stub").is_some());
        assert!(sources.find("nope").is_none());
        let catalog = sources.catalog();
        assert_eq!(catalog[0]["type_id"], "stub");
    }

    #[test]
    fn shapes_stay_stable() {
        source_catalog_shape();
    }

    // The catalog serialises a source's fields and media kinds as JSON.
    fn source_catalog_shape() {
        let mut sources = MediaSources::new();
        sources.register(Box::new(StubSource));
        let catalog = sources.catalog();
        let field = &catalog[0]["fields"][0];
        assert_eq!(field["kind"], "text");
        assert_eq!(catalog[0]["supported_media"][0], "movie");
    }
}