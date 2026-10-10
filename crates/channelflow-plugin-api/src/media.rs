//! The `MediaSource` contract: how a plugin becomes a media library source.
//!
//! A media-source plugin (the Jellyfin sync being the first) implements
//! [`MediaSource`] on top of the ordinary [`Plugin`](crate::plugin::Plugin)
//! lifecycle. It declares the fields a connection form needs, tests a
//! connection's URL and key, lists its libraries, and — when asked — syncs a
//! chosen set of libraries into its own database tables, writing poster and
//! people images under the core's image root. The core shows these sources,
//! stores `Connection`s, and drives sync; the plugin owns its schema, its
//! dedup rules, and the per-version file selection.

use std::path::PathBuf;
use std::sync::Arc;

use async_trait::async_trait;
use axum::Router;
use serde::{Deserialize, Serialize};

use crate::database::PluginDatabase;

/// The kinds of media a source can provide.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum MediaType {
    Movie,
    Series,
    Season,
    Episode,
    Artist,
    Album,
    Track,
    MusicVideo,
}

/// One configured connection to a media-source server.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Connection {
    pub name: String,
    pub url: String,
    #[serde(default)]
    pub api_key: String,
    /// Path remaps: `{ from -> to }` applied to remote paths after sync.
    #[serde(default)]
    pub path_remaps: serde_json::Value,
    /// The user whose libraries this connection reads (optional per source).
    #[serde(default)]
    pub sync_user_id: Option<String>,
    /// Defaults to verifying TLS; turn off for self-signed test servers.
    #[serde(default = "default_true")]
    pub verify_tls: bool,
    /// The server's own identity (e.g. Jellyfin's `Id`), captured when the
    /// connection is tested and used to deep-link into the server's web UI.
    #[serde(default)]
    pub server_id: Option<String>,
    /// The server's display name, captured the same way.
    #[serde(default)]
    pub server_name: Option<String>,
    /// What the media in this connection is (`movie`, `series`, `music`,
    /// `musicvideo`). File-based sources (Local) use it to scan correctly.
    #[serde(default)]
    pub media_kind: Option<String>,
}

fn default_true() -> bool {
    true
}

impl Default for Connection {
    fn default() -> Self {
        Self {
            name: String::new(),
            url: String::new(),
            api_key: String::new(),
            path_remaps: serde_json::json!({}),
            sync_user_id: None,
            verify_tls: true,
            server_id: None,
            server_name: None,
            media_kind: None,
        }
    }
}

/// A single form field a connection form renders for this source.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum FieldSpec {
    Text { key: String, label: String, required: bool },
    Secret { key: String, label: String, required: bool },
    Remaps { key: String, label: String, required: bool },
    Action { key: String, label: String, required: bool },
}

impl FieldSpec {
    pub fn text(key: &str, label: &str) -> Self {
        Self::Text {
            key: key.to_string(),
            label: label.to_string(),
            required: false,
        }
    }

    pub fn secret(key: &str, label: &str) -> Self {
        Self::Secret {
            key: key.to_string(),
            label: label.to_string(),
            required: false,
        }
    }

    pub fn remaps(key: &str, label: &str) -> Self {
        Self::Remaps {
            key: key.to_string(),
            label: label.to_string(),
            required: false,
        }
    }

    pub fn action(key: &str, label: &str) -> Self {
        Self::Action {
            key: key.to_string(),
            label: label.to_string(),
            required: false,
        }
    }

    pub fn required(mut self) -> Self {
        self.set_required(true);
        self
    }

    fn set_required(&mut self, required: bool) {
        match self {
            Self::Text { required: value, .. }
            | Self::Secret { required: value, .. }
            | Self::Remaps { required: value, .. }
            | Self::Action { required: value, .. } => *value = required,
        }
    }
}

/// The answer to "is this URL + key a working server for this source?"
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TestResult {
    pub ok: bool,
    pub code: TestCode,
    pub detail: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TestCode {
    Ok,
    AuthFailed,
    Unreachable,
    BadUrl,
}

impl TestResult {
    pub fn ok(detail: impl Into<String>) -> Self {
        Self {
            ok: true,
            code: TestCode::Ok,
            detail: detail.into(),
        }
    }

    pub fn auth_failed(detail: impl Into<String>) -> Self {
        Self {
            ok: false,
            code: TestCode::AuthFailed,
            detail: detail.into(),
        }
    }

    pub fn unreachable(detail: impl Into<String>) -> Self {
        Self {
            ok: false,
            code: TestCode::Unreachable,
            detail: detail.into(),
        }
    }

    pub fn bad_url(detail: impl Into<String>) -> Self {
        Self {
            ok: false,
            code: TestCode::BadUrl,
            detail: detail.into(),
        }
    }
}

/// A library the server exposes, remote identity + collection type.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Library {
    pub remote_id: String,
    pub name: String,
    #[serde(default)]
    pub collection_type: Option<String>,
}

/// One normalized entry in the base's own media catalog. Any media source can
/// report the items it syncs; the base keeps them so its Media page works the
/// same for every source without asking the live server.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CatalogItem {
    /// Coarse kind: "movie", "series", "album", "artist" or "musicvideo".
    /// The base's Media page groups these into Movies / TV shows / Music /
    /// Music videos tabs.
    pub kind: String,
    /// The source's own stable id for the item (deduplication within a source).
    pub remote_id: String,
    pub title: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub year: Option<i32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub overview: Option<String>,
    /// Absolute path of the poster under the core's image root, if any.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub poster_path: Option<String>,
    /// The library the item was synced from.
    #[serde(default)]
    pub library: String,
    /// Cross-source identity (e.g. `"imdb:tt1375666"` or `"tmdb:27205"`), used
    /// by the base to recognise the same media coming from another source. When
    /// absent the base matches on kind + title + year.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub match_id: Option<String>,
}

impl CatalogItem {
    pub fn new(kind: &str, remote_id: &str, title: &str) -> Self {
        Self {
            kind: kind.to_string(),
            remote_id: remote_id.to_string(),
            title: title.to_string(),
            year: None,
            overview: None,
            poster_path: None,
            library: String::new(),
            match_id: None,
        }
    }

    pub fn year(mut self, year: Option<i32>) -> Self {
        self.year = year;
        self
    }

    pub fn overview(mut self, overview: Option<String>) -> Self {
        self.overview = overview;
        self
    }

    pub fn poster_path(mut self, poster_path: Option<String>) -> Self {
        self.poster_path = poster_path;
        self
    }

    pub fn library(mut self, library: &str) -> Self {
        self.library = library.to_string();
        self
    }

    pub fn match_id(mut self, match_id: Option<String>) -> Self {
        self.match_id = match_id;
        self
    }
}

/// The base's local media catalog. A media-source plugin receives a handle to
/// it during sync and reports the items it found; the base stores them in its
/// own tables and serves them to the Media page. Implemented by the base.
#[async_trait]
pub trait MediaCatalog: Send + Sync {
    /// Replace the catalog rows for one connection + library with `items`
    /// (the result of one sync pass). Returns how many rows are stored.
    async fn replace_library(
        &self,
        connection_id: i64,
        library: &str,
        items: Vec<CatalogItem>,
    ) -> Result<usize, String>;

    /// Remove every catalog row for a connection (the connection was deleted
    /// or the source uninstalled).
    async fn clear_connection(&self, connection_id: i64) -> Result<(), String>;
}

/// Everything a sync pass needs from the core.
#[derive(Clone)]
pub struct SyncCtx {
    /// The connection being synced.
    pub connection: Connection,
    /// This source's secret for that connection.
    pub api_key: String,
    /// The libraries to sync on this pass.
    pub enabled_libraries: Vec<Library>,
    /// The connection's row id in the core's `connections` table.
    pub connection_id: i64,
    /// The plugin's own tables, on the base's Postgres.
    pub db: Arc<dyn PluginDatabase>,
    /// Where posters and people images are written (`<config>/Images`).
    pub image_root: PathBuf,
    /// The connection's path remaps, `{ from -> to }`.
    pub remaps: serde_json::Value,
    /// The base's own media catalog; `None` when no catalog is offered (for
    /// example a plugin-hosted sync run without a base handle).
    pub catalog: Option<Arc<dyn MediaCatalog>>,
}

/// What a sync pass changed.
#[derive(Debug, Default, Clone, Serialize)]
pub struct SyncReport {
    pub added: u64,
    pub updated: u64,
    pub removed: u64,
    pub errors: u64,
}

/// The media-source plugin contract.
#[async_trait]
pub trait MediaSource: Send + Sync {
    /// Stable source id, e.g. `jellyfin`; also the connection `kind`.
    fn type_id(&self) -> &'static str;

    /// Human name shown in the connection picker.
    fn display_name(&self) -> &'static str;

    /// The fields a connection form renders for this source.
    fn connection_fields(&self) -> Vec<FieldSpec>;

    /// What media kinds this source can sync.
    fn supported_media(&self) -> &[MediaType];

    /// Check a connection's URL and key.
    async fn test_connection(&self, connection: &Connection, api_key: &str) -> TestResult;

    /// The libraries the server exposes, for the connection's picker.
    async fn list_libraries(&self, connection: &Connection, api_key: &str) -> Vec<Library>;

    /// Sync the chosen libraries. Implementations update their own database
    /// tables, write images, and report what changed.
    async fn sync_library(&self, ctx: SyncCtx) -> SyncReport;

    /// Identity metadata about the connected server, when the source can say
    /// (e.g. Jellyfin's `{ "server_id": …, "server_name": … }`). The base
    /// stores it on the connection so deep links and labels can use it.
    async fn server_info(
        &self,
        _connection: &Connection,
        _api_key: &str,
    ) -> Option<serde_json::Value> {
        None
    }

    /// A deep link straight to this item on the connected server's web UI
    /// (for example Jellyfin's `…/web/index.html#!/details?id=…`). The Media
    /// page's play button opens it. `None` when the source has no such link.
    fn item_web_url(&self, _connection: &Connection, _remote_id: &str) -> Option<String> {
        None
    }

    /// Extra API routes mounted under `/api/plugins/{id}`, if any.
    fn routes(&self) -> Option<Router> {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn connection_defaults_verify_tls_on() {
        assert!(Connection::default().verify_tls);
        assert_eq!(Connection::default().path_remaps, serde_json::json!({}));
    }

    #[test]
    fn field_specs_mark_required() {
        let name = FieldSpec::text("name", "Connection name").required();
        match name {
            FieldSpec::Text { key, required, .. } => {
                assert_eq!(key, "name");
                assert!(required);
            }
            _ => panic!("expected a text field"),
        }
    }

    #[test]
    fn test_result_carries_the_code() {
        let result = TestResult::auth_failed("nope");
        assert!(!result.ok);
        assert_eq!(result.code, TestCode::AuthFailed);
    }
}