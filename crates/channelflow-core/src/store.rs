//! Storage: channels and each plugin's own namespaced key/value data.
//!
//! Two backends sit behind one `Store`, so how the app persists its settings
//! does not change what the rest of it sees.
//!
//! * **Files** — the default, with zero setup. One JSON document per channel
//!   under `<config>/channels/`, and one file per plugin key under
//!   `<config>/plugins/{plugin}/{key}.json` (written `0600` because plugin
//!   data can hold secrets).
//! * **Postgres** — used when `DATABASE_URL` is set. The same settings live in
//!   tables created at startup: `channels` and `plugin_kv`. On first open
//!   against an empty database the config directory is read once and imported,
//!   so moving to Postgres keeps exactly what the files had; after that
//!   Postgres is the only source of truth and nothing is written to the
//!   directory.
//!
//! Two features grew plugins and their settings moved into plugin storage
//! with a one-time migration: the AI provider list and the transcode settings.
//! The old `ai.json` / `transcode.json` files (and their `ai_settings` /
//! `transcode_settings` rows) are read once and removed by
//! [`Store::upgrade_legacy_ai`] and [`Store::upgrade_legacy_transcode`].

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use async_trait::async_trait;
use channelflow_plugin_api::core::{
    CatalogFilter, CatalogRow, ChatRequest, ChatResponse, CoreChannel, CoreData, CoreDataError,
    LlmProvider, MediaFile, TtsProvider,
};
use channelflow_plugin_api::database::{NoPluginDatabase, PluginDatabase, PluginDatabaseError};
use channelflow_plugin_api::media::{CatalogItem, MediaCatalog};
use channelflow_plugin_api::storage::{PluginStorage, PluginStorageError};
use chrono::{DateTime, Utc};
use sqlx::postgres::{PgPool, PgPoolOptions};
use sqlx::types::Json;
use sqlx::Row;
use uuid::Uuid;

use crate::auth::{AuthRecord, ResetPin};
use crate::model::{Channel, NewChannel, UpdateChannel};
use crate::plugin::registry::PluginRegistry;
use crate::settings::GeneralSettings;

/// The plugin that used to be a core feature — its legacy `ai.json` document
/// is migrated into its own storage under these names.
const LEGACY_AI_NAMESPACE: &str = "com.channelflow.ai";
const LEGACY_AI_KEY: &str = "providers";

/// The transcode plugin and the legacy documents that predate it.
const LEGACY_TRANSCODE_NAMESPACE: &str = "com.channelflow.ersatztv";
const LEGACY_TRANSCODE_DEFAULTS_KEY: &str = "defaults";
const LEGACY_TRANSCODE_OVERRIDES_KEY: &str = "overrides";

/// The core reserves this namespace for its own settings: the plugin
/// repository list, the installed-plugin records, and the compiled-in plugin
/// registry all persist through the same per-plugin key/value store on both
/// backends.
const CORE_NAMESPACE: &str = "com.channelflow.core";
const REPOSITORIES_KEY: &str = "repositories";
const INSTALLED_KEY: &str = "installed";
const PLUGIN_REGISTRY_KEY: &str = "plugin_registry";
const AUTH_KEY: &str = "auth";
const SESSION_KEY: &str = "auth_session";
/// The instance's own API key: one shared secret for the Jellyfin plugin and
/// generic IPTV URLs.
const SERVER_API_KEY: &str = "api_key";
/// The paired ChannelFlow TV apps, each with its own random API key.
const PAIRED_CLIENTS: &str = "paired_clients";
/// How long a login stays valid with no activity. It slides forward on every
/// authenticated request and matches the 30-day login cookie, so an in-use
/// session never expires mid-use and a set-aside one lasts as long as the
/// browser keeps the cookie.
pub const SESSION_TTL_HOURS: i64 = 30 * 24;
const RESET_KEY: &str = "auth_reset";
const RESET_AT_KEY: &str = "auth_reset_at";
const DATABASE_URL_KEY: &str = "database_url";
/// The guide programmes a scheduling pass computed (see `/api/guide`).
const GUIDE_KEY: &str = "guide";
/// The General Settings document (public and local URLs).
const GENERAL_KEY: &str = "general";

/// Storage failures, kept distinct from `anyhow` so the API layer can turn
/// `NotFound` into 404, `DuplicateNumber` into 409 and `Invalid` into 400
/// instead of reporting all of them as 500.
#[derive(Debug)]
pub enum StoreError {
    NotFound(Uuid),
    DuplicateNumber(f64),
    Invalid(&'static str),
    /// A plugin lifecycle call was refused: unknown id, incompatible with this
    /// base, or the plugin's own failure.
    Plugin(String),
    /// A plugin id that is not installed.
    PluginNotFound(String),
    Io(std::io::Error),
    Json(serde_json::Error),
    /// A Postgres round trip failed — connection, statement, or constraint.
    Database(sqlx::Error),
}

impl From<std::io::Error> for StoreError {
    fn from(error: std::io::Error) -> Self {
        StoreError::Io(error)
    }
}

impl From<serde_json::Error> for StoreError {
    fn from(error: serde_json::Error) -> Self {
        StoreError::Json(error)
    }
}

impl From<sqlx::Error> for StoreError {
    fn from(error: sqlx::Error) -> Self {
        StoreError::Database(error)
    }
}

impl std::fmt::Display for StoreError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            StoreError::NotFound(id) => write!(f, "no channel with id {id}"),
            StoreError::DuplicateNumber(n) => write!(f, "channel number {n} is already in use"),
            StoreError::Invalid(msg) => write!(f, "{msg}"),
            StoreError::Plugin(message) => write!(f, "{message}"),
            StoreError::PluginNotFound(id) => write!(f, "plugin {id} is not installed"),
            StoreError::Io(error) => write!(f, "storage error: {error}"),
            StoreError::Json(error) => write!(f, "channel document is not valid JSON: {error}"),
            StoreError::Database(error) => write!(f, "database error: {error}"),
        }
    }
}

impl std::error::Error for StoreError {}

#[derive(Clone)]
pub struct Store {
    /// The config directory: home of the file backend, and the source of the
    /// one-time import into Postgres.
    root: PathBuf,
    /// Shared so `connect_database` can switch every `Store` clone (the ones
    /// the plugins hold included) to Postgres while the app is running.
    backend: std::sync::Arc<tokio::sync::RwLock<Backend>>,
}

#[derive(Clone)]
enum Backend {
    Files,
    Postgres(PgPool),
}

impl Store {
    /// The file backend: `<config>/channels`, seeded so the directory exists
    /// from the start.
    pub fn open(config: &Path) -> Result<Self, StoreError> {
        fs::create_dir_all(config.join("channels"))?;
        Ok(Self {
            root: config.to_path_buf(),
            backend: std::sync::Arc::new(tokio::sync::RwLock::new(Backend::Files)),
        })
    }

    /// The Postgres backend. Connecting, making the schema if it is missing,
    /// and importing the config directory once if the database is empty all
    /// happen here, before the first request.
    pub async fn open_postgres(url: &str, config: &Path) -> Result<Self, StoreError> {
        let pool = PgPoolOptions::new()
            .max_connections(5)
            .connect(url)
            .await?;
        pg::ensure_schema(&pool).await?;
        pg::import_channels(&pool, config).await?;
        Ok(Self {
            root: config.to_path_buf(),
            backend: std::sync::Arc::new(tokio::sync::RwLock::new(Backend::Postgres(pool))),
        })
    }

    /// The currently active backend, for the duration of one call. Taking a
    /// snapshot (rather than holding the lock across the call) is what lets
    /// `connect_database` swap the running backend without anyone having to
    /// coordinate.
    async fn snapshot(&self) -> Backend {
        self.backend.read().await.clone()
    }

    /// Replace one channel's transcode overrides. The caller validates the
    /// patch first; this only stores it. Live use moved to the ErsatzTV
    /// plugin, so the remaining caller is the legacy migration clearing the
    /// patches it moves into plugin storage.
    pub async fn set_channel_transcode(
        &self,
        id: Uuid,
        overrides: serde_json::Value,
    ) -> Result<Channel, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::set_channel_transcode(&self.root, id, overrides),
            Backend::Postgres(pool) => pg::set_channel_transcode(pool, id, overrides).await,
        }
    }

    /// Every channel, ordered by number then name.
    pub async fn list(&self) -> Result<Vec<Channel>, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::list(&self.root),
            Backend::Postgres(pool) => pg::list(pool).await,
        }
    }

    pub async fn get(&self, id: Uuid) -> Result<Channel, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::get(&self.root, id),
            Backend::Postgres(pool) => pg::get(pool, id).await,
        }
    }

    pub async fn create(&self, input: NewChannel) -> Result<Channel, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::create(&self.root, input),
            Backend::Postgres(pool) => pg::create(pool, input).await,
        }
    }

    pub async fn update(&self, id: Uuid, input: UpdateChannel) -> Result<Channel, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::update(&self.root, id, input),
            Backend::Postgres(pool) => pg::update(pool, id, input).await,
        }
    }

    pub async fn delete(&self, id: Uuid) -> Result<(), StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::delete(&self.root, id),
            Backend::Postgres(pool) => pg::delete(pool, id).await,
        }
    }

    /// Create the first channel so a fresh install has something to look at.
    /// Only ever runs against an empty store.
    pub async fn seed(&self) -> Result<Option<Channel>, StoreError> {
        if !self.list().await?.is_empty() {
            return Ok(None);
        }
        let channel = self
            .create(NewChannel {
                number: 1.0,
                name: "ChannelFlow One".to_string(),
                description: "First channel — edit or replace me".to_string(),
                enabled: true,
                watermark: None,
                ai_prompt: String::new(),
            })
            .await?;
        tracing::info!(number = channel.number, name = %channel.name, "seeded first channel");
        Ok(Some(channel))
    }

    // ── plugin key/value storage ───────────────────────────────────────────

    /// Read one key of a plugin's namespaced storage.
    pub async fn plugin_get(
        &self,
        namespace: &str,
        key: &str,
    ) -> Result<Option<serde_json::Value>, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::plugin_get(&self.root, namespace, key),
            Backend::Postgres(pool) => pg::plugin_get(pool, namespace, key).await,
        }
    }

    /// Write one key of a plugin's namespaced storage.
    pub async fn plugin_set(
        &self,
        namespace: &str,
        key: &str,
        value: &serde_json::Value,
    ) -> Result<(), StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::plugin_set(&self.root, namespace, key, value),
            Backend::Postgres(pool) => pg::plugin_set(pool, namespace, key, value).await,
        }
    }

    pub async fn plugin_delete(
        &self,
        namespace: &str,
        key: &str,
    ) -> Result<(), StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::plugin_delete(&self.root, namespace, key),
            Backend::Postgres(pool) => pg::plugin_delete(pool, namespace, key).await,
        }
    }

    /// Task configuration and history, in the core's own namespace beside
    /// auth and the plugin registry.
    pub async fn task_get(&self, key: &str) -> Result<Option<serde_json::Value>, StoreError> {
        self.plugin_get(CORE_NAMESPACE, &format!("task_{key}")).await
    }

    pub async fn task_set(
        &self,
        key: &str,
        value: &serde_json::Value,
    ) -> Result<(), StoreError> {
        self.plugin_set(CORE_NAMESPACE, &format!("task_{key}"), value)
            .await
    }

    /// General settings (the public and local URLs). Stored once in the core
    /// namespace; the local URL is detected on first boot and can be overridden.
    pub async fn general_settings(&self) -> Result<GeneralSettings, StoreError> {
        match self.plugin_get(CORE_NAMESPACE, GENERAL_KEY).await? {
            Some(value) => Ok(serde_json::from_value(value).unwrap_or_default()),
            None => Ok(GeneralSettings::default()),
        }
    }

    pub async fn save_general_settings(
        &self,
        settings: &GeneralSettings,
    ) -> Result<(), StoreError> {
        let value = serde_json::to_value(settings)?;
        self.plugin_set(CORE_NAMESPACE, GENERAL_KEY, &value).await
    }

    /// The guide programmes the scheduling pass stored, if any.
    /// Shape: `{ "programs": [ { id, channel_id, start, finish, title, … } ] }`.
    pub async fn guide_get(&self) -> Result<serde_json::Value, StoreError> {
        Ok(self
            .plugin_get(CORE_NAMESPACE, GUIDE_KEY)
            .await?
            .unwrap_or_else(|| serde_json::json!({ "programs": [] })))
    }

    /// Replace the stored guide programmes.
    pub async fn guide_set(&self, value: &serde_json::Value) -> Result<(), StoreError> {
        self.plugin_set(CORE_NAMESPACE, GUIDE_KEY, value).await
    }

    /// A channel's lineup document, if one was saved. Shape:
    /// `{ "slots": [...], "overrides": [...] }` from the Lineups editor.
    pub async fn lineup_get(&self, channel_id: Uuid) -> Result<Option<serde_json::Value>, StoreError> {
        self.plugin_get(CORE_NAMESPACE, &format!("lineup_{channel_id}"))
            .await
    }

    /// Replace a channel's lineup document.
    pub async fn lineup_set(
        &self,
        channel_id: Uuid,
        value: &serde_json::Value,
    ) -> Result<(), StoreError> {
        self.plugin_set(CORE_NAMESPACE, &format!("lineup_{channel_id}"), value)
            .await
    }

    /// Drop a channel's lineup document (used when the channel is deleted).
    pub async fn lineup_delete(&self, channel_id: Uuid) -> Result<(), StoreError> {
        self.plugin_delete(CORE_NAMESPACE, &format!("lineup_{channel_id}"))
            .await
    }

    /// The storage handle passed to a plugin, scoped to its id.
    pub fn plugin_storage(&self, namespace: &str) -> Arc<dyn PluginStorage> {
        Arc::new(NamespacedStorage {
            store: self.clone(),
            namespace: namespace.to_string(),
        })
    }

    /// Where a plugin's own files live: `<config>/plugins/{id}`.
    pub fn plugin_dir(&self, namespace: &str) -> PathBuf {
        self.root.join("plugins").join(namespace)
    }

    // ── plugin repository and install records ─────────────────────────────

    /// Where an installed plugin's extracted files live:
    /// `<config>/plugins/.installed/{id}`. Kept apart from `plugin_dir` (the
    /// plugin's *runtime data* dir) so replacing a version never touches a
    /// loaded plugin's own storage.
    pub fn installed_dir(&self, id: &str) -> PathBuf {
        self.root.join("plugins").join(".installed").join(id)
    }

    /// Scratch space for downloaded archives: `<config>/plugins/.cache`.
    pub fn cache_dir(&self) -> PathBuf {
        self.root.join("plugins").join(".cache")
    }

    /// The configured plugin-repository URLs, newest first.
    pub async fn repo_list(&self) -> Result<Vec<serde_json::Value>, StoreError> {
        let value = self.plugin_get(CORE_NAMESPACE, REPOSITORIES_KEY).await?;
        Ok(value
            .and_then(|value| value.as_array().cloned())
            .unwrap_or_default())
    }

    /// Register a repository URL. Refuses duplicates.
    pub async fn repo_add(&self, url: &str) -> Result<Vec<serde_json::Value>, StoreError> {
        let mut repos = self.repo_list().await?;
        if repos.iter().any(|repo| repo["url"] == url) {
            return Err(StoreError::Plugin(
                "that repository is already registered".to_string(),
            ));
        }
        repos.push(serde_json::json!({ "id": Uuid::new_v4().to_string(), "url": url }));
        self.plugin_set(
            CORE_NAMESPACE,
            REPOSITORIES_KEY,
            &serde_json::Value::Array(repos.clone()),
        )
        .await?;
        Ok(repos)
    }

    /// Forget one registered repository, by its record id.
    pub async fn repo_remove(&self, id: &str) -> Result<Vec<serde_json::Value>, StoreError> {
        let mut repos = self.repo_list().await?;
        let before = repos.len();
        repos.retain(|repo| repo["id"].as_str() != Some(id));
        if repos.len() == before {
            return Err(StoreError::Plugin("no repository with that id".to_string()));
        }
        self.plugin_set(
            CORE_NAMESPACE,
            REPOSITORIES_KEY,
            &serde_json::Value::Array(repos.clone()),
        )
        .await?;
        Ok(repos)
    }

    /// The installed plugins: one record per plugin id with `version`, `rid`,
    /// `repo`, `installed_at`, and `dir`.
    pub async fn installed_list(&self) -> Result<Vec<serde_json::Value>, StoreError> {
        let value = self.plugin_get(CORE_NAMESPACE, INSTALLED_KEY).await?;
        Ok(value
            .and_then(|value| value.as_array().cloned())
            .unwrap_or_default())
    }

    /// Record a finished install, replacing any earlier record for the id.
    pub async fn record_installed(
        &self,
        entry: serde_json::Value,
    ) -> Result<Vec<serde_json::Value>, StoreError> {
        let id = entry["id"].as_str().unwrap_or_default().to_string();
        let mut installed = self.installed_list().await?;
        installed.retain(|record| record["id"].as_str() != Some(id.as_str()));
        installed.push(entry);
        self.plugin_set(
            CORE_NAMESPACE,
            INSTALLED_KEY,
            &serde_json::Value::Array(installed.clone()),
        )
        .await?;
        Ok(installed)
    }

    /// Replace the whole installed list; the uninstall path uses this after
    /// dropping one entry and its directory.
    pub async fn replace_installed(
        &self,
        installed: Vec<serde_json::Value>,
    ) -> Result<(), StoreError> {
        self.plugin_set(
            CORE_NAMESPACE,
            INSTALLED_KEY,
            &serde_json::Value::Array(installed),
        )
        .await
    }

    // ── media-source connections ───────────────────────────────────────────

    /// Every media-source connection, oldest first.
    pub async fn connection_list(&self) -> Result<Vec<serde_json::Value>, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::connection_list(&self.root),
            Backend::Postgres(pool) => pg::connection_list(pool).await,
        }
    }

    /// Add a connection for a media source. `config` is the source's own
    /// connection object (name, url, api key, remaps, …).
    pub async fn connection_create(
        &self,
        kind: &str,
        config: &serde_json::Value,
    ) -> Result<serde_json::Value, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::connection_create(&self.root, kind, config),
            Backend::Postgres(pool) => pg::connection_create(pool, kind, config).await,
        }
    }

    /// Update a connection's config; the `kind` never changes.
    pub async fn connection_update(
        &self,
        id: i64,
        config: &serde_json::Value,
    ) -> Result<Option<serde_json::Value>, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::connection_update(&self.root, id, config),
            Backend::Postgres(pool) => pg::connection_update(pool, id as i32, config).await,
        }
    }

    /// Remove a connection. Plugins whose tables reference `connections(id)`
    /// cascade with it; the caller is responsible for any post-cascade sweep.
    pub async fn connection_delete(&self, id: i64) -> Result<Option<serde_json::Value>, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::connection_delete(&self.root, id),
            Backend::Postgres(pool) => pg::connection_delete(pool, id as i32).await,
        }
    }

    // ── local media catalog ────────────────────────────────────────────────
    // The base owns one catalog for every media source. A plugin reports the
    // items it synced for a connection + library; the core merges them into one
    // entry per media — matched by a provider id when given, else kind + title
    // + year — and records which sources provide it, so a film that is in both
    // Jellyfin and Plex is a single item with two sources.

    /// Replace the catalog *sources* for one connection + library with `items`
    /// (the result of one sync pass for that library).
    pub async fn media_replace_library(
        &self,
        connection_id: i64,
        library: &str,
        items: &[CatalogItem],
    ) -> Result<usize, StoreError> {
        let source_kind = self
            .connection_kind(connection_id)
            .await?
            .unwrap_or_else(|| "source".to_string());
        match &self.snapshot().await {
            Backend::Files => {
                file::media_replace_library(&self.root, connection_id, &source_kind, library, items)
            }
            Backend::Postgres(pool) => {
                pg::media_replace_library(pool, connection_id, &source_kind, library, items).await
            }
        }
    }

    /// Drop every catalog source for a connection (it was deleted) and prune
    /// any media left with no sources.
    pub async fn media_clear_connection(&self, connection_id: i64) -> Result<(), StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::media_clear_connection(&self.root, connection_id),
            Backend::Postgres(pool) => pg::media_clear_connection(pool, connection_id).await,
        }
    }

    /// Every catalog item with its sources, for the Media page.
    pub async fn media_list(&self) -> Result<Vec<serde_json::Value>, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::media_list(&self.root),
            Backend::Postgres(pool) => pg::media_list(pool).await,
        }
    }

    /// One page of the catalog, filtered to `kinds` (empty = every kind) and an
    /// optional title `query`. Returns the page and the total match count, so
    /// the Media page can lazy-load instead of pulling everything at once.
    pub async fn media_page(
        &self,
        kinds: &[String],
        offset: usize,
        limit: Option<usize>,
        query: &str,
    ) -> Result<(Vec<serde_json::Value>, i64), StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::media_page(&self.root, kinds, offset, limit, query),
            Backend::Postgres(pool) => pg::media_page(pool, kinds, offset, limit, query).await,
        }
    }

    /// How many catalog items there are of each kind (for the tab counts).
    pub async fn media_counts(&self) -> Result<Vec<(String, i64)>, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::media_counts(&self.root),
            Backend::Postgres(pool) => pg::media_counts(pool).await,
        }
    }

    /// The playable files reported for a catalog item, across every source.
    pub async fn media_files_for(
        &self,
        match_key: &str,
    ) -> Result<Vec<serde_json::Value>, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::media_files_for(&self.root, match_key),
            Backend::Postgres(pool) => pg::media_files_for(pool, match_key).await,
        }
    }

    /// The kind (jellyfin / plex / emby / local / …) of a connection.
    pub async fn connection_kind(&self, id: i64) -> Result<Option<String>, StoreError> {
        Ok(self
            .connection_list()
            .await?
            .into_iter()
            .find(|row| row["id"].as_i64() == Some(id))
            .and_then(|row| row["kind"].as_str().map(str::to_string)))
    }

    /// Where media-source plugins write posters and people images:
    /// `<config>/Images`.
    pub fn images_dir(&self) -> PathBuf {
        self.root.join("Images")
    }

    /// The read-only core-data handle handed to plugins that hold
    /// `api:core:read`.
    pub fn core_data(&self, http: reqwest::Client) -> StoreCoreData {
        StoreCoreData {
            store: self.clone(),
            http,
        }
    }

    /// A plugin's own Postgres tables (`storage:database`). On the file
    /// backend this is the no-database double, so a plugin can tell there is
    /// no Postgres and fall back to key/value storage.
    pub async fn plugin_database(&self, namespace: &str) -> Arc<dyn PluginDatabase> {
        match &self.snapshot().await {
            Backend::Files => Arc::new(NoPluginDatabase::default()),
            Backend::Postgres(pool) => Arc::new(PgPluginDatabase {
                pool: pool.clone(),
                prefix: sanitise_table_prefix(namespace),
            }),
        }
    }

    /// The installed/enabled plugin registry.
    pub async fn plugin_registry(&self) -> Result<PluginRegistry, StoreError> {
        match self.plugin_get(CORE_NAMESPACE, PLUGIN_REGISTRY_KEY).await? {
            Some(value) => Ok(serde_json::from_value(value)?),
            None => Ok(PluginRegistry::default()),
        }
    }

    pub async fn save_plugin_registry(&self, registry: &PluginRegistry) -> Result<(), StoreError> {
        let value = serde_json::to_value(registry)?;
        self.plugin_set(CORE_NAMESPACE, PLUGIN_REGISTRY_KEY, &value)
            .await
    }

    /// The web UI's credentials and setup flag.
    pub async fn auth_record(&self) -> Result<Option<AuthRecord>, StoreError> {
        match self.plugin_get(CORE_NAMESPACE, AUTH_KEY).await? {
            Some(value) => Ok(Some(serde_json::from_value(value)?)),
            None => Ok(None),
        }
    }

    pub async fn save_auth_record(&self, record: &AuthRecord) -> Result<(), StoreError> {
        let value = serde_json::to_value(record)?;
        self.plugin_set(CORE_NAMESPACE, AUTH_KEY, &value).await
    }

    /// Whether `token` is the current session and it has not expired. Sessions
    /// live the full login-cookie lifetime (30 days) and the window slides
    /// forward on every authenticated request, so an in-use login never
    /// expires mid-session and a set-aside one lasts as long as the browser
    /// keeps the cookie — there is no short idle timeout.
    pub async fn session_valid(&self, token: &str) -> Result<bool, StoreError> {
        let value = match self.plugin_get(CORE_NAMESPACE, SESSION_KEY).await? {
            Some(serde_json::Value::Object(map)) => map,
            _ => return Ok(false),
        };
        let stored = match value.get("token").and_then(serde_json::Value::as_str) {
            Some(stored) if crate::auth::verify_token(token, stored) => stored.to_string(),
            _ => return Ok(false),
        };
        let expires_at = value
            .get("expires_at")
            .and_then(serde_json::Value::as_str)
            .and_then(|value| DateTime::parse_from_rfc3339(value).ok())
            .map(|value| value.with_timezone(&Utc));
        if expires_at.is_some_and(|expires| Utc::now() >= expires) {
            return Ok(false);
        }
        let _ = self
            .save_session(
                &stored,
                Utc::now() + chrono::Duration::hours(SESSION_TTL_HOURS),
                false,
            )
            .await;
        Ok(true)
    }

    pub async fn save_session(
        &self,
        token: &str,
        expires_at: DateTime<Utc>,
        remembered: bool,
    ) -> Result<(), StoreError> {
        self.plugin_set(
            CORE_NAMESPACE,
            SESSION_KEY,
            &serde_json::json!({
                "token": token,
                "expires_at": expires_at.to_rfc3339(),
                "remembered": remembered,
            }),
        )
        .await
    }

    pub async fn clear_session(&self) -> Result<(), StoreError> {
        self.plugin_delete(CORE_NAMESPACE, SESSION_KEY).await
    }

    // ── API keys ──────────────────────────────────────────────────────────

    /// The instance's shared server API key, generated once and kept.
    pub async fn server_api_key(&self) -> Result<String, StoreError> {
        if let Some(serde_json::Value::String(key)) =
            self.plugin_get(CORE_NAMESPACE, SERVER_API_KEY).await?
        {
            if !key.is_empty() {
                return Ok(key);
            }
        }
        let key = random_api_key();
        self.plugin_set(
            CORE_NAMESPACE,
            SERVER_API_KEY,
            &serde_json::Value::String(key.clone()),
        )
        .await?;
        Ok(key)
    }

    /// Every paired client (their full keys included; callers show only a hint).
    pub async fn client_list(&self) -> Result<Vec<serde_json::Value>, StoreError> {
        Ok(self
            .plugin_get(CORE_NAMESPACE, PAIRED_CLIENTS)
            .await?
            .and_then(|value| value.as_array().cloned())
            .unwrap_or_default())
    }

    /// Pair a new client: mint it a fresh random API key and record it.
    pub async fn client_issue(
        &self,
        label: &str,
    ) -> Result<serde_json::Value, StoreError> {
        let mut clients = self.client_list().await?;
        let client = serde_json::json!({
            "id": Uuid::new_v4().to_string(),
            "api_key": random_api_key(),
            "label": label,
            "created_at": Utc::now().to_rfc3339(),
            "last_seen_at": serde_json::Value::Null,
        });
        clients.push(client.clone());
        self.plugin_set(
            CORE_NAMESPACE,
            PAIRED_CLIENTS,
            &serde_json::Value::Array(clients),
        )
        .await?;
        Ok(client)
    }

    /// Revoke a client by id. `true` when one was removed.
    pub async fn client_remove(&self, id: &str) -> Result<bool, StoreError> {
        let mut clients = self.client_list().await?;
        let before = clients.len();
        clients.retain(|client| client["id"].as_str() != Some(id));
        if clients.len() == before {
            return Ok(false);
        }
        self.plugin_set(
            CORE_NAMESPACE,
            PAIRED_CLIENTS,
            &serde_json::Value::Array(clients),
        )
        .await?;
        Ok(true)
    }

    /// Whether `key` is the server key or any paired client's key.
    pub async fn api_key_valid(&self, key: &str) -> Result<bool, StoreError> {
        if key.is_empty() {
            return Ok(false);
        }
        if keys_equal(key, &self.server_api_key().await?) {
            return Ok(true);
        }
        Ok(self
            .client_list()
            .await?
            .iter()
            .any(|client| keys_equal(key, client["api_key"].as_str().unwrap_or(""))))
    }

    /// A file in the config directory that says the walkthrough has already
    /// been finished. It outlives a backend switch, so a completed install can
    /// never be shown the walkthrough again just because the database was
    /// briefly unreachable.
    pub fn setup_marker_path(&self) -> PathBuf {
        self.root.join("setup-complete")
    }

    pub fn has_setup_marker(&self) -> bool {
        self.setup_marker_path().is_file()
    }

    pub fn write_setup_marker(&self) -> Result<(), StoreError> {
        fs::write(self.setup_marker_path(), "done\n").map_err(StoreError::Io)
    }

    pub fn clear_setup_marker(&self) -> Result<(), StoreError> {
        match fs::remove_file(self.setup_marker_path()) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(StoreError::Io(error)),
        }
    }

    /// Forget the account, session, and installed-plugin registry — the
    /// "reset to first-run setup" that puts an instance back on the
    /// walkthrough. Plugin data and media connections are left alone.
    pub async fn reset_setup(&self) -> Result<(), StoreError> {
        self.plugin_delete(CORE_NAMESPACE, AUTH_KEY).await?;
        self.plugin_delete(CORE_NAMESPACE, SESSION_KEY).await?;
        self.plugin_delete(CORE_NAMESPACE, PLUGIN_REGISTRY_KEY).await
    }

    /// The active password-reset pin, issued by `forgot`.
    pub async fn reset_pin(&self) -> Result<Option<ResetPin>, StoreError> {
        match self.plugin_get(CORE_NAMESPACE, RESET_KEY).await? {
            Some(value) => Ok(Some(serde_json::from_value(value)?)),
            None => Ok(None),
        }
    }

    pub async fn save_reset_pin(&self, reset: &ResetPin) -> Result<(), StoreError> {
        let value = serde_json::to_value(reset)?;
        self.plugin_set(CORE_NAMESPACE, RESET_KEY, &value).await
    }

    pub async fn clear_reset_pin(&self) -> Result<(), StoreError> {
        self.plugin_delete(CORE_NAMESPACE, RESET_KEY).await
    }

    /// When the last password reset was issued, for the cooldown.
    pub async fn last_reset_at(&self) -> Result<Option<chrono::DateTime<Utc>>, StoreError> {
        match self.plugin_get(CORE_NAMESPACE, RESET_AT_KEY).await? {
            Some(value) => Ok(serde_json::from_value(value)?),
            None => Ok(None),
        }
    }

    pub async fn save_last_reset_at(
        &self,
        at: chrono::DateTime<Utc>,
    ) -> Result<(), StoreError> {
        self.plugin_set(
            CORE_NAMESPACE,
            RESET_AT_KEY,
            &serde_json::to_value(at)?,
        )
        .await
    }

    /// The config directory, where reset pin files and plugin data live.
    pub fn config_dir(&self) -> &Path {
        &self.root
    }

    /// The Postgres connection string the walkthrough configured, if any. A
    /// restart uses it the same way `DATABASE_URL` would.
    pub async fn database_url(&self) -> Result<Option<String>, StoreError> {
        match self.plugin_get(CORE_NAMESPACE, DATABASE_URL_KEY).await? {
            Some(serde_json::Value::String(url)) => Ok(Some(url)),
            _ => Ok(None),
        }
    }

    pub async fn save_database_url(&self, url: &str) -> Result<(), StoreError> {
        // Always to the file backend, never the active one: this value is what
        // tells a fresh process which database to open, so it must be readable
        // before any backend is connected.
        file::plugin_set(
            &self.root,
            CORE_NAMESPACE,
            DATABASE_URL_KEY,
            &serde_json::Value::String(url.to_string()),
        )
    }

    /// Connect an already-running (file-backed) instance to Postgres: make the
    /// schema, import anything in the config directory that the database does
    /// not already have, and switch every `Store` clone to the Postgres
    /// backend. This is what the walkthrough's database step calls.
    pub async fn connect_database(&self, url: &str) -> Result<(), StoreError> {
        let pool = PgPoolOptions::new()
            .max_connections(5)
            .acquire_timeout(std::time::Duration::from_secs(10))
            .connect(url)
            .await?;
        pg::ensure_schema(&pool).await?;
        pg::import_channels(&pool, &self.root).await?;
        pg::import_plugin_storage(&pool, &self.root).await?;
        {
            let mut backend = self.backend.write().await;
            *backend = Backend::Postgres(pool);
        }
        tracing::info!("switched the running store to Postgres");
        Ok(())
    }

    /// Erase everything a plugin stored: its key/value files or rows, and the
    /// tables it created. Returns how many tables/keys were dropped. This is
    /// what "remove and drop data" calls; "remove and keep data" skips it.
    pub async fn drop_plugin_data(&self, namespace: &str) -> Result<u64, StoreError> {
        match &self.snapshot().await {
            Backend::Files => file::drop_plugin_data(&self.root, namespace),
            Backend::Postgres(pool) => pg::drop_plugin_data(pool, namespace).await,
        }
    }

    /// One-time move of the legacy `ai.json` (or `ai_settings` row) into the
    /// AI plugin's own storage. Only runs when that storage is empty, and
    /// removes the legacy copy once moved.
    pub async fn upgrade_legacy_ai(&self) -> Result<(), StoreError> {
        if self
            .plugin_get(LEGACY_AI_NAMESPACE, LEGACY_AI_KEY)
            .await?
            .is_some()
        {
            return Ok(());
        }
        let legacy = match &self.snapshot().await {
            Backend::Files => file::read_legacy_ai(&self.root)?,
            Backend::Postgres(pool) => pg::read_legacy_ai(pool).await?,
        };
        let Some(value) = legacy else {
            return Ok(());
        };
        self.plugin_set(LEGACY_AI_NAMESPACE, LEGACY_AI_KEY, &value)
            .await?;
        tracing::info!(plugin = LEGACY_AI_NAMESPACE, "migrated legacy AI settings into plugin storage");
        match &self.snapshot().await {
            Backend::Files => file::remove_legacy_ai(&self.root)?,
            Backend::Postgres(pool) => pg::remove_legacy_ai(pool).await?,
        }
        Ok(())
    }

    /// One-time move of the old transcode settings into the ErsatzTV plugin's
    /// own storage: the instance defaults under `defaults`, and each channel's
    /// override patch under `overrides` (a channel-id map). Channel patches
    /// are cleared here so the plugin's copy is the only one.
    pub async fn upgrade_legacy_transcode(&self) -> Result<(), StoreError> {
        if self
            .plugin_get(LEGACY_TRANSCODE_NAMESPACE, LEGACY_TRANSCODE_DEFAULTS_KEY)
            .await?
            .is_none()
        {
            let legacy = match &self.snapshot().await {
                Backend::Files => file::read_legacy_transcode(&self.root)?,
                Backend::Postgres(pool) => pg::read_legacy_transcode(pool).await?,
            };
            if let Some(value) = legacy {
                self.plugin_set(
                    LEGACY_TRANSCODE_NAMESPACE,
                    LEGACY_TRANSCODE_DEFAULTS_KEY,
                    &value,
                )
                .await?;
            }
        }

        if self
            .plugin_get(LEGACY_TRANSCODE_NAMESPACE, LEGACY_TRANSCODE_OVERRIDES_KEY)
            .await?
            .is_none()
        {
            let mut overrides = serde_json::Map::new();
            let mut to_clear = Vec::new();
            for channel in self.list().await? {
                if let Some(patch) = channel.transcode.as_object() {
                    if !patch.is_empty() {
                        overrides.insert(channel.id.to_string(), channel.transcode);
                        to_clear.push(channel.id);
                    }
                }
            }
            if !overrides.is_empty() {
                self.plugin_set(
                    LEGACY_TRANSCODE_NAMESPACE,
                    LEGACY_TRANSCODE_OVERRIDES_KEY,
                    &serde_json::Value::Object(overrides),
                )
                .await?;
                for id in to_clear {
                    self.set_channel_transcode(
                        id,
                        serde_json::Value::Object(serde_json::Map::new()),
                    )
                    .await?;
                }
            }
        }

        match &self.snapshot().await {
            Backend::Files => file::remove_legacy_transcode(&self.root)?,
            Backend::Postgres(pool) => pg::remove_legacy_transcode(pool).await?,
        }
        tracing::info!(plugin = LEGACY_TRANSCODE_NAMESPACE, "migrated legacy transcode settings into plugin storage");
        Ok(())
    }
}

// The core's own media catalog: media-source plugins report what they synced
// and the base stores it (see `Store::media_*`), so the Media page is the same
// for every source. The catalog handle is handed to syncs even though it is
// driven by sync itself, so a plugin never needs the base's Store.
#[async_trait]
impl MediaCatalog for Store {
    async fn replace_library(
        &self,
        connection_id: i64,
        library: &str,
        items: Vec<CatalogItem>,
    ) -> Result<usize, String> {
        self.media_replace_library(connection_id, library, &items)
            .await
            .map_err(|error| error.to_string())
    }

    async fn clear_connection(&self, connection_id: i64) -> Result<(), String> {
        self.media_clear_connection(connection_id)
            .await
            .map_err(|error| error.to_string())
    }
}

/// The read-only view of channels handed to plugins that hold `api:core:read`.
#[derive(Clone)]
pub struct StoreCoreData {
    store: Store,
    http: reqwest::Client,
}

#[async_trait]
impl CoreData for StoreCoreData {
    async fn channels(&self) -> Result<Vec<CoreChannel>, CoreDataError> {
        let channels = self
            .store
            .list()
            .await
            .map_err(|error| CoreDataError(error.to_string()))?;
        Ok(channels
            .into_iter()
            .map(|channel| CoreChannel {
                id: channel.id.to_string(),
                number: channel.number,
                name: channel.name,
                enabled: channel.enabled,
            })
            .collect())
    }

    async fn installed_plugins(&self) -> Result<Vec<String>, CoreDataError> {
        let registry = self
            .store
            .plugin_registry()
            .await
            .map_err(|error| CoreDataError(error.to_string()))?;
        Ok(registry
            .installed
            .iter()
            .map(|installed| installed.id.clone())
            .collect())
    }

    async fn set_channel_guide(
        &self,
        channel_id: &str,
        programs: Vec<serde_json::Value>,
    ) -> Result<(), CoreDataError> {
        let guide = self
            .store
            .guide_get()
            .await
            .map_err(|error| CoreDataError(error.to_string()))?;
        let mut kept: Vec<serde_json::Value> = guide["programs"]
            .as_array()
            .cloned()
            .unwrap_or_default()
            .into_iter()
            .filter(|program| program["channel_id"].as_str() != Some(channel_id))
            .collect();
        for mut program in programs {
            if let Some(object) = program.as_object_mut() {
                object.insert(
                    "channel_id".to_string(),
                    serde_json::Value::String(channel_id.to_string()),
                );
            }
            kept.push(program);
        }
        let document = serde_json::json!({ "programs": kept });
        self.store
            .guide_set(&document)
            .await
            .map_err(|error| CoreDataError(error.to_string()))
    }

    async fn tts_provider(&self) -> Result<Option<TtsProvider>, CoreDataError> {
        // A dedicated TTS provider (tts_only) beats one that merely names a
        // tts_model; within a class the lowest priority number wins.
        let providers = self.ai_providers().await?;
        let mut best: Option<&serde_json::Value> = None;
        let mut best_rank = (u8::MAX, u64::MAX);
        for provider in &providers {
            if !provider
                .get("enabled")
                .and_then(serde_json::Value::as_bool)
                .unwrap_or(true)
            {
                continue;
            }
            let field = |name: &str| {
                provider
                    .get(name)
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or("")
            };
            if field("tts_model").trim().is_empty() || field("base_url").trim().is_empty() {
                continue;
            }
            let class = if provider
                .get("tts_only")
                .and_then(serde_json::Value::as_bool)
                .unwrap_or(false)
            {
                0
            } else {
                1
            };
            let priority = provider
                .get("priority")
                .and_then(serde_json::Value::as_u64)
                .unwrap_or(u64::MAX);
            if best.is_none() || (class, priority) < best_rank {
                best = Some(provider);
                best_rank = (class, priority);
            }
        }
        let Some(provider) = best else {
            return Ok(None);
        };
        let field = |name: &str| {
            provider
                .get(name)
                .and_then(serde_json::Value::as_str)
                .unwrap_or("")
                .trim()
                .to_string()
        };
        Ok(Some(TtsProvider {
            base_url: field("base_url").trim_end_matches('/').to_string(),
            api_key: field("api_key"),
            tts_model: field("tts_model"),
            voice: field("voice"),
        }))
    }

    async fn llm_provider(&self) -> Result<Option<LlmProvider>, CoreDataError> {
        // The lowest-numbered enabled provider that can write text (not
        // TTS-only, with a chat model named).
        let providers = self.ai_providers().await?;
        let mut best: Option<&serde_json::Value> = None;
        let mut best_priority = u64::MAX;
        for provider in &providers {
            if !provider
                .get("enabled")
                .and_then(serde_json::Value::as_bool)
                .unwrap_or(true)
            {
                continue;
            }
            if provider
                .get("tts_only")
                .and_then(serde_json::Value::as_bool)
                .unwrap_or(false)
            {
                continue;
            }
            let field = |name: &str| {
                provider
                    .get(name)
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or("")
            };
            if field("chat_model").trim().is_empty() || field("base_url").trim().is_empty() {
                continue;
            }
            let priority = provider
                .get("priority")
                .and_then(serde_json::Value::as_u64)
                .unwrap_or(u64::MAX);
            if best.is_none() || priority < best_priority {
                best = Some(provider);
                best_priority = priority;
            }
        }
        let Some(provider) = best else {
            return Ok(None);
        };
        let field = |name: &str| {
            provider
                .get(name)
                .and_then(serde_json::Value::as_str)
                .unwrap_or("")
                .trim()
                .to_string()
        };
        Ok(Some(LlmProvider {
            id: field("id").trim().to_string(),
            name: field("name").trim().to_string(),
            base_url: field("base_url").trim_end_matches('/').to_string(),
            api_key: field("api_key"),
            chat_model: field("chat_model"),
            temperature: provider
                .get("temperature")
                .and_then(serde_json::Value::as_f64)
                .map(|value| value as f32),
        }))
    }

    async fn catalog(&self, filter: CatalogFilter) -> Result<Vec<CatalogRow>, CoreDataError> {
        let entries = self
            .store
            .media_list()
            .await
            .map_err(|error| CoreDataError(error.to_string()))?;
        let keys: std::collections::HashSet<&str> =
            filter.match_keys.iter().map(String::as_str).collect();
        let ceiling = filter.rating_max.as_deref().map(rating_rank);
        let mut rows = Vec::new();
        for entry in &entries {
            let match_key = entry["match_key"].as_str().unwrap_or("");
            if match_key.is_empty() {
                continue;
            }
            if !keys.is_empty() && !keys.contains(match_key) {
                continue;
            }
            let kind = entry["kind"].as_str().unwrap_or("");
            if filter.kind.as_deref().is_some_and(|want| want != kind) {
                continue;
            }
            let year = entry["year"].as_i64().map(|value| value as i32);
            if filter
                .year_min
                .is_some_and(|min| year.map_or(true, |value| value < min))
            {
                continue;
            }
            if filter
                .year_max
                .is_some_and(|max| year.map_or(true, |value| value > max))
            {
                continue;
            }
            let genres = json_strings(&entry["genres"]);
            if !filter.genres.is_empty() && !filter.genres.iter().any(|want| genres.contains(want)) {
                continue;
            }
            let tags = json_strings(&entry["tags"]);
            if !filter.tags.is_empty() && !filter.tags.iter().any(|want| tags.contains(want)) {
                continue;
            }
            let rating = entry["official_rating"].as_str();
            if let Some(ceiling) = ceiling {
                if rating.map(rating_rank).is_some_and(|rank| rank > ceiling) {
                    continue;
                }
            }
            let detail = &entry["detail"];
            rows.push(CatalogRow {
                match_key: match_key.to_string(),
                kind: kind.to_string(),
                title: entry["title"].as_str().unwrap_or("").to_string(),
                year,
                runtime_ms: entry["runtime_seconds"].as_i64().map(|seconds| seconds * 1000),
                genres,
                official_rating: rating.map(str::to_string),
                tags,
                series_key: detail
                    .get("series_id")
                    .and_then(serde_json::Value::as_str)
                    .map(str::to_string),
                season: detail
                    .get("parent_index_number")
                    .and_then(serde_json::Value::as_i64)
                    .map(|value| value as i32),
                episode: detail
                    .get("index_number")
                    .and_then(serde_json::Value::as_i64)
                    .map(|value| value as i32),
            });
            if filter
                .limit
                .is_some_and(|limit| rows.len() >= limit as usize)
            {
                break;
            }
        }
        Ok(rows)
    }

    async fn lineup(&self, channel_id: &str) -> Result<Option<serde_json::Value>, CoreDataError> {
        let Ok(id) = Uuid::parse_str(channel_id) else {
            return Ok(None);
        };
        self.store
            .lineup_get(id)
            .await
            .map_err(|error| CoreDataError(error.to_string()))
    }

    async fn llm_providers(&self) -> Result<Vec<LlmProvider>, CoreDataError> {
        let providers = self.ai_providers().await?;
        let mut ordered: Vec<(u64, LlmProvider)> = Vec::new();
        for provider in &providers {
            if !provider
                .get("enabled")
                .and_then(serde_json::Value::as_bool)
                .unwrap_or(true)
            {
                continue;
            }
            if provider
                .get("tts_only")
                .and_then(serde_json::Value::as_bool)
                .unwrap_or(false)
            {
                continue;
            }
            let field = |name: &str| {
                provider
                    .get(name)
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or("")
            };
            if field("chat_model").trim().is_empty() || field("base_url").trim().is_empty() {
                continue;
            }
            let priority = provider
                .get("priority")
                .and_then(serde_json::Value::as_u64)
                .unwrap_or(u64::MAX);
            ordered.push((
                priority,
                LlmProvider {
                    id: field("id").trim().to_string(),
                    name: field("name").trim().to_string(),
                    base_url: field("base_url").trim().trim_end_matches('/').to_string(),
                    api_key: field("api_key").to_string(),
                    chat_model: field("chat_model").trim().to_string(),
                    temperature: provider
                        .get("temperature")
                        .and_then(serde_json::Value::as_f64)
                        .map(|value| value as f32),
                },
            ));
        }
        ordered.sort_by_key(|(priority, _)| *priority);
        Ok(ordered.into_iter().map(|(_, provider)| provider).collect())
    }

    async fn chat(&self, request: ChatRequest) -> Result<ChatResponse, CoreDataError> {
        let providers = self.llm_providers().await?;
        if providers.is_empty() {
            return Err(CoreDataError(
                "no AI provider is configured for scheduling".to_string(),
            ));
        }
        let mut attempts = Vec::new();
        for provider in &providers {
            match self.chat_once(provider, &request).await {
                Ok(content) => {
                    return Ok(ChatResponse {
                        content,
                        provider_id: provider.id.clone(),
                        attempts,
                    });
                }
                Err(ChatFailure::Fatal(message)) => return Err(CoreDataError(message)),
                Err(ChatFailure::Retry(message)) => {
                    let label = if provider.name.is_empty() {
                        provider.id.clone()
                    } else {
                        provider.name.clone()
                    };
                    attempts.push(format!("{label}: {message}"));
                }
            }
        }
        Err(CoreDataError(format!(
            "every AI provider failed — {}",
            attempts.join("; ")
        )))
    }

    async fn file_for(&self, match_key: &str) -> Result<Option<MediaFile>, CoreDataError> {
        let files = self
            .store
            .media_files_for(match_key)
            .await
            .map_err(|error| CoreDataError(error.to_string()))?;
        for file in files {
            let path = file["path"].as_str().unwrap_or("").trim();
            if path.is_empty() {
                continue;
            }
            return Ok(Some(MediaFile {
                source_kind: file["source_kind"].as_str().unwrap_or("").to_string(),
                path: path.to_string(),
                runtime_ms: file["runtime_seconds"].as_i64().map(|seconds| seconds * 1000),
            }));
        }
        Ok(None)
    }
}

/// Why a chat attempt did not produce content: `Fatal` means the request shape
/// is wrong and every provider would reject it, so the walk stops; `Retry`
/// means the next provider should be tried.
enum ChatFailure {
    Retry(String),
    Fatal(String),
}

/// The string values in a JSON array (an item's genres or tags).
fn json_strings(value: &serde_json::Value) -> Vec<String> {
    value
        .as_array()
        .map(|items| {
            items
                .iter()
                .filter_map(|item| item.as_str().map(str::to_string))
                .collect()
        })
        .unwrap_or_default()
}

/// A coarse ordinal for the common MPAA/TV ratings, so a caller can cap what
/// the catalog hands back. Unknown ratings rank lowest (never filtered out).
fn rating_rank(rating: &str) -> u8 {
    match rating.trim().to_ascii_uppercase().as_str() {
        "TV-Y" | "G" | "EC" => 1,
        "TV-Y7" => 2,
        "TV-G" | "PG" => 3,
        "TV-PG" => 4,
        "PG-13" => 5,
        "TV-14" => 6,
        "R" => 7,
        "NC-17" | "TV-MA" | "X" => 8,
        _ => 0,
    }
}

impl StoreCoreData {
    /// The AI page's provider list, or empty when the plugin is not installed.
    /// The AI plugin keeps it in its own namespaced storage.
    async fn ai_providers(&self) -> Result<Vec<serde_json::Value>, CoreDataError> {
        let value = self
            .store
            .plugin_get(LEGACY_AI_NAMESPACE, LEGACY_AI_KEY)
            .await
            .map_err(|error| CoreDataError(error.to_string()))?;
        Ok(value
            .and_then(|value| {
                value
                    .get("providers")
                    .and_then(serde_json::Value::as_array)
                    .cloned()
            })
            .unwrap_or_default())
    }

    /// One provider's turn at a chat completion. Tries with `response_format`
    /// first when JSON was asked for, then once without it (some compatible
    /// servers reject the field), and classifies the failure for the walk.
    async fn chat_once(
        &self,
        provider: &LlmProvider,
        request: &ChatRequest,
    ) -> Result<String, ChatFailure> {
        let modes: Vec<bool> = if request.json {
            vec![true, false]
        } else {
            vec![false]
        };
        let last_index = modes.len() - 1;
        let mut last = ChatFailure::Retry("no attempt was made".to_string());
        for (index, with_json) in modes.iter().enumerate() {
            match self.chat_attempt(provider, request, *with_json).await {
                Ok(content) => return Ok(content),
                Err(failure) => {
                    let retry_without_json =
                        matches!(failure, ChatFailure::Retry(_)) && index < last_index;
                    last = failure;
                    if retry_without_json {
                        continue;
                    }
                    break;
                }
            }
        }
        Err(last)
    }

    async fn chat_attempt(
        &self,
        provider: &LlmProvider,
        request: &ChatRequest,
        with_json: bool,
    ) -> Result<String, ChatFailure> {
        let model = request
            .model
            .clone()
            .filter(|model| !model.trim().is_empty())
            .unwrap_or_else(|| provider.chat_model.clone());
        let messages: Vec<serde_json::Value> = request
            .messages
            .iter()
            .map(|message| {
                serde_json::json!({ "role": message.role, "content": message.content })
            })
            .collect();
        let mut body = serde_json::json!({ "model": model, "messages": messages });
        if let Some(temperature) = request.temperature.or(provider.temperature) {
            body["temperature"] = serde_json::json!(temperature);
        }
        if with_json {
            body["response_format"] = serde_json::json!({ "type": "json_object" });
        }
        let url = format!("{}/chat/completions", provider.base_url.trim_end_matches('/'));
        let mut builder = self
            .http
            .post(&url)
            .header(reqwest::header::CONTENT_TYPE, "application/json")
            // A local model can take a while to load on the first call; give a
            // real completion far longer than the shared client's 30s.
            .timeout(std::time::Duration::from_secs(180))
            .body(body.to_string());
        if !provider.api_key.is_empty() {
            builder = builder.bearer_auth(&provider.api_key);
        }
        let response = builder
            .send()
            .await
            .map_err(|error| ChatFailure::Retry(cause(&error)))?;
        let status = response.status();
        let text = response.text().await.unwrap_or_default();
        if !status.is_success() {
            let message = http_message(status, &text);
            return Err(
                if status == reqwest::StatusCode::BAD_REQUEST
                    || status == reqwest::StatusCode::UNPROCESSABLE_ENTITY
                {
                    ChatFailure::Fatal(message)
                } else {
                    ChatFailure::Retry(message)
                },
            );
        }
        let value: serde_json::Value = serde_json::from_str(&text)
            .map_err(|_| ChatFailure::Retry("the endpoint did not answer with JSON".to_string()))?;
        value
            .pointer("/choices/0/message/content")
            .and_then(serde_json::Value::as_str)
            .map(str::trim)
            .filter(|content| !content.is_empty())
            .map(str::to_string)
            .ok_or_else(|| ChatFailure::Retry("the response carried no message content".to_string()))
    }
}

/// The deepest cause of a request error, not reqwest's wrapper sentence.
fn cause(error: &reqwest::Error) -> String {
    let mut cause: &dyn std::error::Error = error;
    while let Some(source) = cause.source() {
        cause = source;
    }
    cause.to_string()
}

/// A one-line HTTP failure: the endpoint's own message when it sends JSON.
fn http_message(status: reqwest::StatusCode, body: &str) -> String {
    let message = serde_json::from_str::<serde_json::Value>(body)
        .ok()
        .and_then(|value| {
            value
                .pointer("/error/message")
                .or_else(|| value.pointer("/message"))
                .and_then(serde_json::Value::as_str)
                .map(str::to_string)
        })
        .unwrap_or_else(|| {
            let trimmed = body.trim();
            if trimmed.is_empty() {
                "no message".to_string()
            } else {
                trimmed.chars().take(200).collect()
            }
        });
    format!("HTTP {} — {message}", status.as_u16())
}

/// A plugin's own tables, prefixed with its id so no two namespaces can
/// collide. `create_table("audit", …)` runs as
/// `CREATE TABLE IF NOT EXISTS cf_com_channelflow_ai_audit (…)` for the AI
/// plugin.
pub struct PgPluginDatabase {
    pool: PgPool,
    prefix: String,
}

#[async_trait]
impl PluginDatabase for PgPluginDatabase {
    fn table_of(&self, name: &str) -> Option<String> {
        if is_identifier(name) {
            Some(format!("{}_{}", self.prefix, name))
        } else {
            None
        }
    }

    async fn create_table(&self, name: &str, columns: &str) -> Result<(), PluginDatabaseError> {
        let table = self
            .table_of(name)
            .ok_or_else(|| PluginDatabaseError(format!("\"{name}\" is not a table name")))?;
        let sql = format!("CREATE TABLE IF NOT EXISTS {table} ({columns})");
        self.execute(&sql).await.map(|_| ())
    }

    async fn execute(&self, sql: &str) -> Result<u64, PluginDatabaseError> {
        sqlx::query(sql)
            .execute(&self.pool)
            .await
            .map(|result| result.rows_affected())
            .map_err(database_error)
    }

    async fn execute_params(
        &self,
        sql: &str,
        params: &[serde_json::Value],
    ) -> Result<u64, PluginDatabaseError> {
        let mut query = sqlx::query(sql);
        for param in params {
            query = bind_param(query, param);
        }
        query
            .execute(&self.pool)
            .await
            .map(|result| result.rows_affected())
            .map_err(database_error)
    }

    async fn fetch(&self, sql: &str) -> Result<Vec<serde_json::Value>, PluginDatabaseError> {
        // A data-modifying CTE (`WITH t AS (INSERT … RETURNING …)`) lets the
        // same wrapper serve SELECTs and RETURNING statements alike.
        let wrapped = format!("WITH t AS ({sql}) SELECT COALESCE(json_agg(row_to_json(t)), '[]'::json) FROM t");
        let row = sqlx::query(&wrapped)
            .fetch_one(&self.pool)
            .await
            .map_err(database_error)?;
        let value: serde_json::Value = row
            .try_get::<Json<serde_json::Value>, _>(0)
            .map_err(database_error)?
            .0;
        match value {
            serde_json::Value::Array(rows) => Ok(rows),
            _ => Ok(Vec::new()),
        }
    }

    async fn fetch_params(
        &self,
        sql: &str,
        params: &[serde_json::Value],
    ) -> Result<Vec<serde_json::Value>, PluginDatabaseError> {
        let wrapped = format!("WITH t AS ({sql}) SELECT COALESCE(json_agg(row_to_json(t)), '[]'::json) FROM t");
        let mut query = sqlx::query(&wrapped);
        for param in params {
            query = bind_param(query, param);
        }
        let row = query
            .fetch_one(&self.pool)
            .await
            .map_err(database_error)?;
        let value: serde_json::Value = row
            .try_get::<Json<serde_json::Value>, _>(0)
            .map_err(database_error)?
            .0;
        match value {
            serde_json::Value::Array(rows) => Ok(rows),
            _ => Ok(Vec::new()),
        }
    }
}

/// Bind one JSON parameter as text. `null` becomes SQL NULL; strings pass
/// through; booleans/numbers use their JSON text form; the SQL casts where a
/// typed column needs it (`$1::int`, `$1::bool`).
fn bind_param<'a>(
    query: sqlx::query::Query<'a, sqlx::Postgres, sqlx::postgres::PgArguments>,
    param: &serde_json::Value,
) -> sqlx::query::Query<'a, sqlx::Postgres, sqlx::postgres::PgArguments> {
    match param {
        serde_json::Value::Null => query.bind(None::<String>),
        serde_json::Value::String(text) => query.bind(text.clone()),
        other => query.bind(other.to_string()),
    }
}

fn database_error(error: sqlx::Error) -> PluginDatabaseError {
    PluginDatabaseError(error.to_string())
}

/// A plugin id becomes a safe table prefix: `com.channelflow.ai` ->
/// `cf_com_channelflow_ai`. Anything that is not alphanumeric becomes a single
/// underscore, so distinct ids keep distinct names.
fn sanitise_table_prefix(namespace: &str) -> String {
    let mut prefix = String::from("cf_");
    for character in namespace.chars() {
        if character.is_ascii_alphanumeric() {
            prefix.push(character.to_ascii_lowercase());
        } else if !prefix.ends_with('_') {
            prefix.push('_');
        }
    }
    prefix.trim_end_matches('_').to_string()
}

/// Only plain SQL identifiers are allowed as plugin table names, so a name
/// can never smuggle arbitrary DDL into the prefix.
fn is_identifier(name: &str) -> bool {
    !name.is_empty()
        && name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
}

/// Plugin namespaces are dotted ids. Reject anything that could climb out of
/// the plugins directory or otherwise be used as a path.
fn is_safe_namespace(namespace: &str) -> bool {
    !namespace.is_empty() && !namespace.contains(['/', '\\']) && !namespace.contains("..")
}

/// The `PluginStorage` view of a `Store`, scoped to one plugin id.
struct NamespacedStorage {
    store: Store,
    namespace: String,
}

#[async_trait]
impl PluginStorage for NamespacedStorage {
    async fn get(&self, key: &str) -> Result<Option<serde_json::Value>, PluginStorageError> {
        self.store
            .plugin_get(&self.namespace, key)
            .await
            .map_err(|error| PluginStorageError(error.to_string()))
    }

    async fn set(&self, key: &str, value: &serde_json::Value) -> Result<(), PluginStorageError> {
        self.store
            .plugin_set(&self.namespace, key, value)
            .await
            .map_err(|error| PluginStorageError(error.to_string()))
    }

    async fn delete(&self, key: &str) -> Result<(), PluginStorageError> {
        self.store
            .plugin_delete(&self.namespace, key)
            .await
            .map_err(|error| PluginStorageError(error.to_string()))
    }
}

/// The identity the base matches a media item on, so the same title from two
/// sources is one catalog entry. A provider id (e.g. `imdb:tt1375666`) wins;
/// otherwise kind + normalized title + year.
fn match_key(kind: &str, match_id: Option<&str>, title: &str, year: Option<i32>) -> String {
    if let Some(id) = match_id.map(str::trim).filter(|id| !id.is_empty()) {
        return format!("{kind}:{}", id.to_ascii_lowercase());
    }
    let normalized: String = title
        .to_ascii_lowercase()
        .chars()
        .filter(|c| c.is_alphanumeric())
        .collect();
    format!("{kind}:{normalized}:{}", year.unwrap_or(0))
}

/// A random 64-character hex API key (32 random bytes).
pub fn random_api_key() -> String {
    let mut bytes = Vec::with_capacity(32);
    bytes.extend_from_slice(Uuid::new_v4().as_bytes());
    bytes.extend_from_slice(Uuid::new_v4().as_bytes());
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

/// Constant-time string equality, so key checks do not leak length-by-length.
pub fn keys_equal(left: &str, right: &str) -> bool {
    let (left, right) = (left.as_bytes(), right.as_bytes());
    if left.len() != right.len() {
        return false;
    }
    let mut diff = 0u8;
    for (a, b) in left.iter().zip(right) {
        diff |= a ^ b;
    }
    diff == 0
}

/// The file backend. Writing goes through a temp file plus rename, so a
/// crash halfway never leaves a half-written document behind.
mod file {
    use super::*;

    fn channel_path(root: &Path, id: Uuid) -> PathBuf {
        root.join("channels").join(format!("{id}.json"))
    }

    pub fn read_legacy_transcode(root: &Path) -> Result<Option<serde_json::Value>, StoreError> {
        let path = root.join("transcode.json");
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(error.into()),
        };
        Ok(Some(serde_json::from_str(&text)?))
    }

    pub fn remove_legacy_transcode(root: &Path) -> Result<(), StoreError> {
        match fs::remove_file(root.join("transcode.json")) {
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
            other => other.map_err(Into::into),
        }
    }

    pub fn set_channel_transcode(
        root: &Path,
        id: Uuid,
        overrides: serde_json::Value,
    ) -> Result<Channel, StoreError> {
        let mut channel = get(root, id)?;
        channel.transcode = if overrides.is_null() {
            serde_json::Value::Object(serde_json::Map::new())
        } else {
            overrides
        };
        channel.updated_at = Utc::now();
        write(root, &channel)?;
        Ok(channel)
    }

    pub fn list(root: &Path) -> Result<Vec<Channel>, StoreError> {
        let mut channels = Vec::new();
        for entry in fs::read_dir(root.join("channels"))? {
            let entry = entry?;
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("json") {
                continue;
            }
            let text = fs::read_to_string(&path)?;
            match serde_json::from_str::<Channel>(&text) {
                Ok(channel) => channels.push(channel),
                Err(error) => {
                    tracing::warn!(path = %path.display(), %error, "skipping unreadable channel");
                }
            }
        }
        channels.sort_by(|a, b| {
            a.number
                .partial_cmp(&b.number)
                .unwrap_or(std::cmp::Ordering::Equal)
                .then_with(|| a.name.cmp(&b.name))
        });
        Ok(channels)
    }

    pub fn get(root: &Path, id: Uuid) -> Result<Channel, StoreError> {
        let text = fs::read_to_string(channel_path(root, id))
            .map_err(|_| StoreError::NotFound(id))?;
        serde_json::from_str(&text).map_err(|_| StoreError::NotFound(id))
    }

    pub fn create(root: &Path, input: NewChannel) -> Result<Channel, StoreError> {
        let input = checked_new(input)?;
        reserve(root, input.number, None)?;

        let now = Utc::now();
        let channel = Channel {
            id: Uuid::new_v4(),
            number: input.number,
            name: input.name,
            description: input.description,
            enabled: input.enabled,
            watermark: input.watermark,
            ai_prompt: input.ai_prompt,
            transcode: serde_json::Value::Object(serde_json::Map::new()),
            created_at: now,
            updated_at: now,
        };
        write(root, &channel)?;
        Ok(channel)
    }

    pub fn update(root: &Path, id: Uuid, input: UpdateChannel) -> Result<Channel, StoreError> {
        let mut channel = get(root, id)?;
        if let Some(number) = input.number {
            if number == 0.0 {
                return Err(StoreError::Invalid("channel number must be at least 1"));
            }
            reserve(root, number, Some(id))?;
            channel.number = number;
        }
        if input.watermark.is_some() {
            channel.watermark = input.watermark.unwrap_or(None);
        }
        if let Some(ai_prompt) = input.ai_prompt {
            channel.ai_prompt = ai_prompt;
        }
        if let Some(name) = input.name {
            let name = name.trim();
            if name.is_empty() {
                return Err(StoreError::Invalid("channel name cannot be empty"));
            }
            channel.name = name.to_string();
        }
        if let Some(description) = input.description {
            channel.description = description.trim().to_string();
        }
        if let Some(enabled) = input.enabled {
            channel.enabled = enabled;
        }
        channel.updated_at = Utc::now();
        write(root, &channel)?;
        Ok(channel)
    }

    pub fn delete(root: &Path, id: Uuid) -> Result<(), StoreError> {
        fs::remove_file(channel_path(root, id)).map_err(|_| StoreError::NotFound(id))
    }

    fn reserve(root: &Path, number: f64, ignore: Option<Uuid>) -> Result<(), StoreError> {
        for existing in list(root)? {
            if Some(existing.id) == ignore {
                continue;
            }
            if existing.number == number {
                return Err(StoreError::DuplicateNumber(number));
            }
        }
        Ok(())
    }

    fn write(root: &Path, channel: &Channel) -> Result<(), StoreError> {
        let path = channel_path(root, channel.id);
        let tmp = path.with_extension("json.tmp");
        let json = serde_json::to_string_pretty(channel)?;
        fs::write(&tmp, json)?;
        fs::rename(&tmp, &path)?;
        Ok(())
    }

    fn plugin_path(root: &Path, namespace: &str, key: &str) -> PathBuf {
        root.join("plugins").join(namespace).join(format!("{key}.json"))
    }

    pub fn plugin_get(
        root: &Path,
        namespace: &str,
        key: &str,
    ) -> Result<Option<serde_json::Value>, StoreError> {
        let path = plugin_path(root, namespace, key);
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(error.into()),
        };
        Ok(Some(serde_json::from_str(&text)?))
    }

    pub fn plugin_set(
        root: &Path,
        namespace: &str,
        key: &str,
        value: &serde_json::Value,
    ) -> Result<(), StoreError> {
        let path = plugin_path(root, namespace, key);
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let json = serde_json::to_string_pretty(value)?;
        write_private(&path, &json)?;
        Ok(())
    }

    pub fn plugin_delete(
        root: &Path,
        namespace: &str,
        key: &str,
    ) -> Result<(), StoreError> {
        match fs::remove_file(plugin_path(root, namespace, key)) {
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
            other => other.map_err(Into::into),
        }
    }

    // ── media-source connections ───────────────────────────────────────────
    // One JSON document under `<config>/connections.json` (written 0600; the
    // configs carry API keys). Postgres keeps the same rows in `connections`,
    // which the media-source plugins' own tables reference.

    fn connections_path(root: &Path) -> PathBuf {
        root.join("connections.json")
    }

    fn read_connections(root: &Path) -> Result<Vec<serde_json::Value>, StoreError> {
        let path = connections_path(root);
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
            Err(error) => return Err(error.into()),
        };
        let value: serde_json::Value = serde_json::from_str(&text)?;
        Ok(value.as_array().cloned().unwrap_or_default())
    }

    fn write_connections(root: &Path, connections: &[serde_json::Value]) -> Result<(), StoreError> {
        let json = serde_json::to_string_pretty(&serde_json::Value::Array(connections.to_vec()))?;
        write_private(&connections_path(root), &json)?;
        Ok(())
    }

    pub fn connection_list(root: &Path) -> Result<Vec<serde_json::Value>, StoreError> {
        read_connections(root)
    }

    pub fn connection_create(
        root: &Path,
        kind: &str,
        config: &serde_json::Value,
    ) -> Result<serde_json::Value, StoreError> {
        let mut connections = read_connections(root)?;
        let next = connections
            .iter()
            .map(|connection| connection["id"].as_i64().unwrap_or(0))
            .max()
            .unwrap_or(0)
            + 1;
        let connection = serde_json::json!({ "id": next, "kind": kind, "config": config });
        connections.push(connection.clone());
        write_connections(root, &connections)?;
        Ok(connection)
    }

    pub fn connection_update(
        root: &Path,
        id: i64,
        config: &serde_json::Value,
    ) -> Result<Option<serde_json::Value>, StoreError> {
        let mut connections = read_connections(root)?;
        let Some(index) = connections
            .iter()
            .position(|connection| connection["id"].as_i64() == Some(id))
        else {
            return Ok(None);
        };
        let kind = connections[index]["kind"]
            .as_str()
            .unwrap_or_default()
            .to_string();
        let connection = serde_json::json!({ "id": id, "kind": kind, "config": config });
        connections[index] = connection.clone();
        write_connections(root, &connections)?;
        Ok(Some(connection))
    }

    pub fn connection_delete(
        root: &Path,
        id: i64,
    ) -> Result<Option<serde_json::Value>, StoreError> {
        let mut connections = read_connections(root)?;
        let Some(index) = connections
            .iter()
            .position(|connection| connection["id"].as_i64() == Some(id))
        else {
            return Ok(None);
        };
        let removed = connections.remove(index);
        write_connections(root, &connections)?;
        Ok(Some(removed))
    }

    /// Remove a plugin's whole storage directory.
    pub fn drop_plugin_data(root: &Path, namespace: &str) -> Result<u64, StoreError> {
        if !is_safe_namespace(namespace) {
            return Err(StoreError::Invalid("that is not a plugin namespace"));
        }
        let dir = root.join("plugins").join(namespace);
        if !dir.exists() {
            return Ok(0);
        }
        let count = fs::read_dir(&dir).map(|entries| entries.count()).unwrap_or(0) as u64;
        fs::remove_dir_all(&dir)?;
        Ok(count)
    }

    // ── local media catalog ────────────────────────────────────────────────
    // `<config>/media_catalog.json`: `{ "items": [ { match_key, kind, title,
    // year, overview, poster_path, sources: [ { source_kind, connection_id,
    // library, remote_id, poster_path } ] } ] }`. Postgres keeps the same data
    // in `media_items` + `media_item_sources`.

    fn media_catalog_path(root: &Path) -> PathBuf {
        root.join("media_catalog.json")
    }

    fn read_media_catalog(root: &Path) -> Result<Vec<serde_json::Value>, StoreError> {
        let path = media_catalog_path(root);
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
            Err(error) => return Err(error.into()),
        };
        let value: serde_json::Value = serde_json::from_str(&text)?;
        Ok(value
            .get("items")
            .and_then(|items| items.as_array())
            .cloned()
            .unwrap_or_default())
    }

    fn write_media_catalog(root: &Path, items: &[serde_json::Value]) -> Result<(), StoreError> {
        let json = serde_json::to_string_pretty(&serde_json::json!({ "items": items }))?;
        write_private(&media_catalog_path(root), &json)?;
        Ok(())
    }

    fn has_sources(entry: &serde_json::Value) -> bool {
        entry["sources"]
            .as_array()
            .is_some_and(|sources| !sources.is_empty())
    }

    pub fn media_replace_library(
        root: &Path,
        connection_id: i64,
        source_kind: &str,
        library: &str,
        items: &[CatalogItem],
    ) -> Result<usize, StoreError> {
        let mut catalog = read_media_catalog(root)?;
        for entry in catalog.iter_mut() {
            if let Some(sources) = entry.get_mut("sources").and_then(serde_json::Value::as_array_mut) {
                sources.retain(|source| {
                    !(source["source_kind"].as_str() == Some(source_kind)
                        && source["connection_id"].as_i64() == Some(connection_id)
                        && source["library"].as_str() == Some(library))
                });
            }
        }
        for item in items {
            let key = super::match_key(&item.kind, item.match_id.as_deref(), &item.title, item.year);
            let source = serde_json::json!({
                "source_kind": source_kind,
                "connection_id": connection_id,
                "library": library,
                "remote_id": item.remote_id,
                "poster_path": item.poster_path,
            });
            let files: Vec<serde_json::Value> = item
                .files
                .iter()
                .map(|file| {
                    serde_json::json!({
                        "source_kind": source_kind,
                        "connection_id": connection_id,
                        "library": library,
                        "item_remote_id": item.remote_id,
                        "remote_id": file.remote_id,
                        "path": file.path,
                        "container": file.container,
                        "size_bytes": file.size_bytes,
                        "runtime_seconds": file.runtime_seconds,
                    })
                })
                .collect();
            match catalog
                .iter_mut()
                .find(|entry| entry["match_key"].as_str() == Some(key.as_str()))
            {
                Some(entry) => {
                    entry["title"] = serde_json::json!(item.title);
                    if item.year.is_some() {
                        entry["year"] = serde_json::json!(item.year);
                    }
                    if item.overview.is_some() {
                        entry["overview"] = serde_json::json!(item.overview);
                    }
                    if item.poster_path.is_some() {
                        entry["poster_path"] = serde_json::json!(item.poster_path);
                    }
                    if item.runtime_seconds.is_some() {
                        entry["runtime_seconds"] = serde_json::json!(item.runtime_seconds);
                    }
                    if !item.genres.is_empty() {
                        entry["genres"] = serde_json::json!(item.genres);
                    }
                    if item.official_rating.is_some() {
                        entry["official_rating"] = serde_json::json!(item.official_rating);
                    }
                    if item.premiere_date.is_some() {
                        entry["premiere_date"] = serde_json::json!(item.premiere_date);
                    }
                    if item.first_episode_year.is_some() {
                        entry["first_episode_year"] = serde_json::json!(item.first_episode_year);
                    }
                    if !item.studios.is_empty() {
                        entry["studios"] = serde_json::json!(item.studios);
                    }
                    if !item.tags.is_empty() {
                        entry["tags"] = serde_json::json!(item.tags);
                    }
                    if let Some(detail) = &item.detail {
                        entry["detail"] = detail.clone();
                    }
                    if !item.people.is_empty() {
                        entry["people"] = serde_json::to_value(&item.people).unwrap_or(serde_json::Value::Null);
                    }
                    let already = entry["sources"].as_array().is_some_and(|sources| {
                        sources.iter().any(|source| {
                            source["source_kind"].as_str() == Some(source_kind)
                                && source["connection_id"].as_i64() == Some(connection_id)
                                && source["remote_id"].as_str() == Some(item.remote_id.as_str())
                        })
                    });
                    if !already {
                        if let Some(sources) = entry["sources"].as_array_mut() {
                            sources.push(source);
                        } else {
                            entry["sources"] = serde_json::json!([source]);
                        }
                    }
                    if !files.is_empty() {
                        if let Some(list) = entry["files"].as_array_mut() {
                            list.retain(|file| {
                                !(file["source_kind"].as_str() == Some(source_kind)
                                    && file["connection_id"].as_i64() == Some(connection_id)
                                    && file["library"].as_str() == Some(library)
                                    && file["item_remote_id"].as_str()
                                        == Some(item.remote_id.as_str()))
                            });
                            list.extend(files);
                        } else {
                            entry["files"] = serde_json::json!(files);
                        }
                    }
                }
                None => catalog.push(serde_json::json!({
                    "match_key": key,
                    "kind": item.kind,
                    "title": item.title,
                    "year": item.year,
                    "overview": item.overview,
                    "poster_path": item.poster_path,
                    "runtime_seconds": item.runtime_seconds,
                    "genres": item.genres,
                    "official_rating": item.official_rating,
                    "premiere_date": item.premiere_date,
                    "first_episode_year": item.first_episode_year,
                    "studios": item.studios,
                    "tags": item.tags,
                    "detail": item.detail,
                    "people": item.people,
                    "files": files,
                    "sources": [source],
                })),
            }
        }
        catalog.retain(has_sources);
        write_media_catalog(root, &catalog)?;
        Ok(items.len())
    }

    pub fn media_clear_connection(root: &Path, connection_id: i64) -> Result<(), StoreError> {
        let mut catalog = read_media_catalog(root)?;
        for entry in catalog.iter_mut() {
            if let Some(sources) = entry.get_mut("sources").and_then(serde_json::Value::as_array_mut) {
                sources.retain(|source| source["connection_id"].as_i64() != Some(connection_id));
            }
            if let Some(files) = entry.get_mut("files").and_then(serde_json::Value::as_array_mut) {
                files.retain(|file| file["connection_id"].as_i64() != Some(connection_id));
            }
        }
        catalog.retain(has_sources);
        write_media_catalog(root, &catalog)
    }

    pub fn media_files_for(
        root: &Path,
        match_key: &str,
    ) -> Result<Vec<serde_json::Value>, StoreError> {
        let catalog = read_media_catalog(root)?;
        Ok(catalog
            .iter()
            .find(|entry| entry["match_key"].as_str() == Some(match_key))
            .and_then(|entry| entry["files"].as_array().cloned())
            .unwrap_or_default())
    }

    pub fn media_list(root: &Path) -> Result<Vec<serde_json::Value>, StoreError> {
        read_media_catalog(root)
    }

    pub fn media_counts(root: &Path) -> Result<Vec<(String, i64)>, StoreError> {
        let mut counts: std::collections::BTreeMap<String, i64> = std::collections::BTreeMap::new();
        for item in read_media_catalog(root)? {
            if let Some(kind) = item["kind"].as_str() {
                *counts.entry(kind.to_string()).or_insert(0) += 1;
            }
        }
        Ok(counts.into_iter().collect())
    }

    pub fn media_page(
        root: &Path,
        kinds: &[String],
        offset: usize,
        limit: Option<usize>,
        query: &str,
    ) -> Result<(Vec<serde_json::Value>, i64), StoreError> {
        let query = query.trim().to_ascii_lowercase();
        let mut items: Vec<serde_json::Value> = read_media_catalog(root)?
            .into_iter()
            .filter(|item| {
                let kind_ok = kinds.is_empty()
                    || item["kind"].as_str().is_some_and(|kind| kinds.iter().any(|want| want == kind));
                let query_ok =
                    query.is_empty() || item["title"].as_str().unwrap_or("").to_ascii_lowercase().contains(&query);
                kind_ok && query_ok
            })
            .collect();
        items.sort_by(|a, b| {
            let key = |item: &serde_json::Value| {
                (
                    item["kind"].as_str().unwrap_or("").to_string(),
                    item["title"].as_str().unwrap_or("").to_string(),
                )
            };
            key(a).cmp(&key(b))
        });
        let total = items.len() as i64;
        let page = items
            .into_iter()
            .skip(offset)
            .take(limit.unwrap_or(usize::MAX))
            .collect();
        Ok((page, total))
    }

    pub fn read_legacy_ai(root: &Path) -> Result<Option<serde_json::Value>, StoreError> {
        let path = root.join("ai.json");
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(error.into()),
        };
        Ok(Some(serde_json::from_str(&text)?))
    }

    pub fn remove_legacy_ai(root: &Path) -> Result<(), StoreError> {
        let path = root.join("ai.json");
        match fs::remove_file(&path) {
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
            other => other.map_err(Into::into),
        }
    }
}

/// The Postgres backend. The tables are invented here and owned by
/// ChannelFlow; next's own schema lives in the same server untouched.
    /// Normalise a JSONB array column back to `Vec<String>`.
    fn json_array(value: serde_json::Value) -> Vec<String> {
        match value {
            serde_json::Value::Array(items) => items
                .into_iter()
                .filter_map(|item| match item {
                    serde_json::Value::String(text) => Some(text),
                    _ => None,
                })
                .collect(),
            _ => Vec::new(),
        }
    }

mod pg {
    use super::*;

    pub async fn ensure_schema(pool: &PgPool) -> Result<(), StoreError> {
        for statement in [
            "CREATE TABLE IF NOT EXISTS channels (
                id UUID PRIMARY KEY,
                number DOUBLE PRECISION NOT NULL UNIQUE,
                name TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                ai_prompt TEXT NOT NULL DEFAULT '',
                enabled BOOLEAN NOT NULL DEFAULT TRUE,
                transcode JSONB NOT NULL DEFAULT '{}'::jsonb,
                watermark JSONB NOT NULL DEFAULT 'null'::jsonb,
                created_at TIMESTAMPTZ NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL
            )",
            "CREATE TABLE IF NOT EXISTS plugin_kv (
                namespace TEXT NOT NULL,
                key TEXT NOT NULL,
                value JSONB NOT NULL,
                PRIMARY KEY (namespace, key)
            )",
            // Media-source connections, shared by every media-source plugin
            // (the Jellyfin schema's tables reference connections(id)).
            "CREATE TABLE IF NOT EXISTS connections (
                id SERIAL PRIMARY KEY,
                kind TEXT NOT NULL,
                config JSONB NOT NULL DEFAULT '{}'::jsonb,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )",
            // The base's own media catalog: one row per media (matched across
            // sources) plus the sources that provide it. Plugins never read
            // these tables — they feed the base Media page, which must work the
            // same for any source.
            "DROP TABLE IF EXISTS media_catalog",
            "CREATE TABLE IF NOT EXISTS media_items (
                id BIGSERIAL PRIMARY KEY,
                match_key TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                year INTEGER,
                overview TEXT,
                poster_path TEXT,
                runtime_seconds BIGINT,
                genres JSONB DEFAULT '[]'::jsonb,
                official_rating TEXT,
                premiere_date TEXT,
                first_episode_year INTEGER,
                studios JSONB DEFAULT '[]'::jsonb,
                tags JSONB DEFAULT '[]'::jsonb,
                detail JSONB,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )",
            "CREATE TABLE IF NOT EXISTS media_item_sources (
                item_id BIGINT NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
                source_kind TEXT NOT NULL,
                connection_id BIGINT NOT NULL,
                library TEXT NOT NULL,
                remote_id TEXT NOT NULL,
                poster_path TEXT,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (item_id, source_kind, connection_id, library, remote_id)
            )",
            "CREATE INDEX IF NOT EXISTS media_item_sources_conn \
                ON media_item_sources (connection_id, library)",
            "CREATE TABLE IF NOT EXISTS media_people (
                id BIGSERIAL PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                image_path TEXT
            )",
            "CREATE TABLE IF NOT EXISTS media_item_people (
                item_id BIGINT NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
                person_id BIGINT NOT NULL REFERENCES media_people(id) ON DELETE CASCADE,
                role_type TEXT NOT NULL DEFAULT '',
                role TEXT NOT NULL DEFAULT '',
                character TEXT NOT NULL DEFAULT '',
                sort_order INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (item_id, person_id, role_type, role)
            )",
            "CREATE INDEX IF NOT EXISTS media_item_people_item_idx \
                ON media_item_people (item_id, sort_order)",
            "CREATE TABLE IF NOT EXISTS media_files (
                item_id BIGINT NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
                source_kind TEXT NOT NULL,
                connection_id BIGINT NOT NULL,
                library TEXT NOT NULL,
                remote_id TEXT,
                path TEXT NOT NULL,
                container TEXT,
                size_bytes BIGINT,
                runtime_seconds BIGINT,
                streams JSONB,
                chapters JSONB,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (item_id, source_kind, connection_id, library, path)
            )",
            "CREATE INDEX IF NOT EXISTS media_files_item_idx ON media_files (item_id)",
            "CREATE INDEX IF NOT EXISTS media_files_conn_idx \
                ON media_files (connection_id, library)",
        ] {
            sqlx::query(statement).execute(pool).await?;
        }
        // Older installs declare an INTEGER dial number; widen it so
        // subchannel numbers (119.1) fit. A no-op once it already is.
        let _ = sqlx::query(
            "ALTER TABLE channels ALTER COLUMN number TYPE DOUBLE PRECISION USING number::double precision",
        )
        .execute(pool)
        .await;
        // Older installs lack the extended media metadata columns.
        for column in [
            ("media_items", "runtime_seconds", "BIGINT"),
            ("media_items", "genres", "JSONB DEFAULT '[]'::jsonb"),
            ("media_items", "official_rating", "TEXT"),
            ("media_items", "premiere_date", "TEXT"),
            ("media_items", "first_episode_year", "INTEGER"),
            ("media_items", "studios", "JSONB DEFAULT '[]'::jsonb"),
            ("media_items", "tags", "JSONB DEFAULT '[]'::jsonb"),
            ("media_items", "detail", "JSONB"),
        ] {
            let statement = format!(
                "ALTER TABLE {} ADD COLUMN IF NOT EXISTS {} {}",
                column.0, column.1, column.2
            );
            let _ = sqlx::query(&statement).execute(pool).await;
        }
        // The watermark overlay column for installs that predate it.
        let _ = sqlx::query(
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS watermark JSONB NOT NULL DEFAULT 'null'::jsonb",
        )
        .execute(pool)
        .await;
        // The per-channel AI scheduling prompt for installs that predate it.
        let _ = sqlx::query(
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS ai_prompt TEXT NOT NULL DEFAULT ''",
        )
        .execute(pool)
        .await;
        Ok(())
    }

    /// The pre-plugin `transcode_settings` row, if the old schema is still
    /// around — an existing database keeps it until the plugin migration.
    pub async fn read_legacy_transcode(
        pool: &PgPool,
    ) -> Result<Option<serde_json::Value>, StoreError> {
        let legacy: i64 = sqlx::query(
            "SELECT count(*) FROM information_schema.tables \
             WHERE table_schema = 'public' AND table_name = 'transcode_settings'",
        )
        .fetch_one(pool)
        .await?
        .try_get(0)?;
        if legacy == 0 {
            return Ok(None);
        }
        match sqlx::query("SELECT config FROM transcode_settings WHERE id = 1")
            .fetch_optional(pool)
            .await?
        {
            Some(row) => Ok(Some(row.try_get::<Json<serde_json::Value>, _>("config")?.0)),
            None => Ok(None),
        }
    }

    pub async fn remove_legacy_transcode(pool: &PgPool) -> Result<(), StoreError> {
        sqlx::query("DROP TABLE IF EXISTS transcode_settings")
            .execute(pool)
            .await?;
        Ok(())
    }

    /// Channels from the config directory are imported once, only into an
    /// empty `channels` table, so a database that already has data is never
    /// overwritten.
    pub async fn import_channels(pool: &PgPool, config: &Path) -> Result<(), StoreError> {
        let count: i64 = sqlx::query("SELECT count(*) FROM channels")
            .fetch_one(pool)
            .await?
            .try_get(0)?;
        if count > 0 {
            return Ok(());
        }
        let dir = config.join("channels");
        let entries = match fs::read_dir(&dir) {
            Ok(entries) => entries,
            Err(_) => return Ok(()),
        };
        let mut imported = 0i64;
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("json") {
                continue;
            }
            let text = fs::read_to_string(&path)?;
            match serde_json::from_str::<Channel>(&text) {
                Ok(channel) => {
                    insert(pool, &channel).await?;
                    imported += 1;
                }
                Err(error) => {
                    tracing::warn!(path = %path.display(), %error, "skipping unreadable channel");
                }
            }
        }
        if imported > 0 {
            tracing::info!(imported, "migrated channels from file into Postgres");
        }
        Ok(())
    }

    /// Copy every plugin's key/value file into `plugin_kv`, once, only when
    /// the table is empty. This carries over the settings that predate the
    /// database — the plugin registry, auth, repositories, and each plugin's
    /// own data — so switching backends mid-run keeps everything.
    pub async fn import_plugin_storage(
        pool: &PgPool,
        config: &Path,
    ) -> Result<(), StoreError> {
        let count: i64 = sqlx::query("SELECT count(*) FROM plugin_kv")
            .fetch_one(pool)
            .await?
            .try_get(0)?;
        if count > 0 {
            return Ok(());
        }
        let base = config.join("plugins");
        let entries = match fs::read_dir(&base) {
            Ok(entries) => entries,
            Err(_) => return Ok(()),
        };
        let mut imported = 0i64;
        for namespace in entries.flatten() {
            if !namespace.file_type().map(|t| t.is_dir()).unwrap_or(false) {
                continue;
            }
            let namespace = namespace.file_name().to_string_lossy().into_owned();
            for key in fs::read_dir(base.join(&namespace)).into_iter().flatten() {
                let entry = match key {
                    Ok(entry) => entry,
                    Err(_) => continue,
                };
                let path = entry.path();
                if path.extension().and_then(|e| e.to_str()) != Some("json") {
                    continue;
                }
                let Some(key_name) = path
                    .file_stem()
                    .and_then(|name| name.to_str())
                    .map(str::to_string)
                else {
                    continue;
                };
                let text = fs::read_to_string(&path)?;
                let value: serde_json::Value = serde_json::from_str(&text)?;
                plugin_set(pool, &namespace, &key_name, &value).await?;
                imported += 1;
            }
        }
        if imported > 0 {
            tracing::info!(imported, "migrated plugin storage into Postgres");
        }
        Ok(())
    }

    pub async fn list(pool: &PgPool) -> Result<Vec<Channel>, StoreError> {
        let rows = sqlx::query(channel_columns())
            .fetch_all(pool)
            .await?;
        rows.iter().map(row_to_channel).collect()
    }

    pub async fn get(pool: &PgPool, id: Uuid) -> Result<Channel, StoreError> {
        let row = sqlx::query(&format!("{} WHERE id = $1", channel_columns()))
            .bind(id)
            .fetch_optional(pool)
            .await?
            .ok_or(StoreError::NotFound(id))?;
        row_to_channel(&row)
    }

    pub async fn create(pool: &PgPool, input: NewChannel) -> Result<Channel, StoreError> {
        let input = checked_new(input)?;
        reserve(pool, input.number, None).await?;

        let now = Utc::now();
        let channel = Channel {
            id: Uuid::new_v4(),
            number: input.number,
            name: input.name,
            description: input.description,
            enabled: input.enabled,
            watermark: input.watermark,
            ai_prompt: input.ai_prompt,
            transcode: serde_json::Value::Object(serde_json::Map::new()),
            created_at: now,
            updated_at: now,
        };
        insert(pool, &channel).await?;
        Ok(channel)
    }

    pub async fn update(
        pool: &PgPool,
        id: Uuid,
        input: UpdateChannel,
    ) -> Result<Channel, StoreError> {
        let mut channel = get(pool, id).await?;
        if let Some(number) = input.number {
            if number == 0.0 {
                return Err(StoreError::Invalid("channel number must be at least 1"));
            }
            reserve(pool, number, Some(id)).await?;
            channel.number = number;
        }
        if input.watermark.is_some() {
            channel.watermark = input.watermark.unwrap_or(None);
        }
        if let Some(ai_prompt) = input.ai_prompt {
            channel.ai_prompt = ai_prompt;
        }
        if let Some(name) = input.name {
            let name = name.trim();
            if name.is_empty() {
                return Err(StoreError::Invalid("channel name cannot be empty"));
            }
            channel.name = name.to_string();
        }
        if let Some(description) = input.description {
            channel.description = description.trim().to_string();
        }
        if let Some(enabled) = input.enabled {
            channel.enabled = enabled;
        }
        channel.updated_at = Utc::now();
        update_row(pool, &channel).await?;
        Ok(channel)
    }

    pub async fn delete(pool: &PgPool, id: Uuid) -> Result<(), StoreError> {
        let result = sqlx::query("DELETE FROM channels WHERE id = $1")
            .bind(id)
            .execute(pool)
            .await?;
        if result.rows_affected() == 0 {
            return Err(StoreError::NotFound(id));
        }
        Ok(())
    }

    pub async fn set_channel_transcode(
        pool: &PgPool,
        id: Uuid,
        overrides: serde_json::Value,
    ) -> Result<Channel, StoreError> {
        let mut channel = get(pool, id).await?;
        channel.transcode = if overrides.is_null() {
            serde_json::Value::Object(serde_json::Map::new())
        } else {
            overrides
        };
        channel.updated_at = Utc::now();
        update_row(pool, &channel).await?;
        Ok(channel)
    }

    pub async fn plugin_get(
        pool: &PgPool,
        namespace: &str,
        key: &str,
    ) -> Result<Option<serde_json::Value>, StoreError> {
        match sqlx::query("SELECT value FROM plugin_kv WHERE namespace = $1 AND key = $2")
            .bind(namespace)
            .bind(key)
            .fetch_optional(pool)
            .await?
        {
            Some(row) => Ok(Some(row.try_get::<Json<serde_json::Value>, _>("value")?.0)),
            None => Ok(None),
        }
    }

    pub async fn plugin_set(
        pool: &PgPool,
        namespace: &str,
        key: &str,
        value: &serde_json::Value,
    ) -> Result<(), StoreError> {
        sqlx::query(
            "INSERT INTO plugin_kv (namespace, key, value) VALUES ($1, $2, $3)
             ON CONFLICT (namespace, key) DO UPDATE SET value = EXCLUDED.value",
        )
        .bind(namespace)
        .bind(key)
        .bind(Json(value))
        .execute(pool)
        .await?;
        Ok(())
    }

    pub async fn plugin_delete(
        pool: &PgPool,
        namespace: &str,
        key: &str,
    ) -> Result<(), StoreError> {
        sqlx::query("DELETE FROM plugin_kv WHERE namespace = $1 AND key = $2")
            .bind(namespace)
            .bind(key)
            .execute(pool)
            .await?;
        Ok(())
    }

    // ── media-source connections ───────────────────────────────────────────

    pub async fn connection_list(pool: &PgPool) -> Result<Vec<serde_json::Value>, StoreError> {
        let rows = sqlx::query("SELECT id, kind, config FROM connections ORDER BY id")
            .fetch_all(pool)
            .await?;
        let mut connections = Vec::new();
        for row in rows {
            let id: i32 = row.try_get("id")?;
            let kind: String = row.try_get("kind")?;
            let config: serde_json::Value =
                row.try_get::<Json<serde_json::Value>, _>("config")?.0;
            connections.push(serde_json::json!({ "id": id, "kind": kind, "config": config }));
        }
        Ok(connections)
    }

    pub async fn connection_create(
        pool: &PgPool,
        kind: &str,
        config: &serde_json::Value,
    ) -> Result<serde_json::Value, StoreError> {
        let row = sqlx::query(
            "INSERT INTO connections (kind, config) VALUES ($1, $2) RETURNING id, kind, config",
        )
        .bind(kind)
        .bind(Json(config))
        .fetch_one(pool)
        .await?;
        let id: i32 = row.try_get("id")?;
        let config: serde_json::Value = row.try_get::<Json<serde_json::Value>, _>("config")?.0;
        Ok(serde_json::json!({ "id": id, "kind": kind, "config": config }))
    }

    pub async fn connection_update(
        pool: &PgPool,
        id: i32,
        config: &serde_json::Value,
    ) -> Result<Option<serde_json::Value>, StoreError> {
        let row = sqlx::query(
            "UPDATE connections SET config = $1 WHERE id = $2 RETURNING id, kind, config",
        )
        .bind(Json(config))
        .bind(id)
        .fetch_optional(pool)
        .await?;
        match row {
            Some(row) => {
                let kind: String = row.try_get("kind")?;
                let config: serde_json::Value =
                    row.try_get::<Json<serde_json::Value>, _>("config")?.0;
                Ok(Some(serde_json::json!({ "id": id, "kind": kind, "config": config })))
            }
            None => Ok(None),
        }
    }

    pub async fn connection_delete(
        pool: &PgPool,
        id: i32,
    ) -> Result<Option<serde_json::Value>, StoreError> {
        let row = sqlx::query("DELETE FROM connections WHERE id = $1 RETURNING id, kind")
            .bind(id)
            .fetch_optional(pool)
            .await?;
        match row {
            Some(row) => {
                let kind: String = row.try_get("kind")?;
                Ok(Some(serde_json::json!({ "id": id, "kind": kind })))
            }
            None => Ok(None),
        }
    }

    // ── local media catalog ────────────────────────────────────────────────

    pub async fn media_replace_library(
        pool: &PgPool,
        connection_id: i64,
        source_kind: &str,
        library: &str,
        items: &[CatalogItem],
    ) -> Result<usize, StoreError> {
        let mut tx = pool.begin().await?;
        sqlx::query(
            "DELETE FROM media_item_sources \
             WHERE source_kind = $1 AND connection_id = $2 AND library = $3",
        )
        .bind(source_kind)
        .bind(connection_id)
        .bind(library)
        .execute(&mut *tx)
        .await?;
        sqlx::query(
            "DELETE FROM media_files \
             WHERE source_kind = $1 AND connection_id = $2 AND library = $3",
        )
        .bind(source_kind)
        .bind(connection_id)
        .bind(library)
        .execute(&mut *tx)
        .await?;
        for item in items {
            let key = super::match_key(&item.kind, item.match_id.as_deref(), &item.title, item.year);
            let row = sqlx::query(
                "INSERT INTO media_items (match_key, kind, title, year, overview, poster_path, \
                     runtime_seconds, genres, official_rating, premiere_date, first_episode_year, studios, tags, detail, updated_at) \
                 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, now()) \
                 ON CONFLICT (match_key) DO UPDATE SET \
                     title = EXCLUDED.title, \
                     year = COALESCE(EXCLUDED.year, media_items.year), \
                     overview = COALESCE(EXCLUDED.overview, media_items.overview), \
                     poster_path = COALESCE(EXCLUDED.poster_path, media_items.poster_path), \
                     runtime_seconds = COALESCE(EXCLUDED.runtime_seconds, media_items.runtime_seconds), \
                     genres = CASE WHEN jsonb_array_length(EXCLUDED.genres) > 0 THEN EXCLUDED.genres ELSE media_items.genres END, \
                     official_rating = COALESCE(EXCLUDED.official_rating, media_items.official_rating), \
                     premiere_date = COALESCE(EXCLUDED.premiere_date, media_items.premiere_date), \
                     first_episode_year = COALESCE(EXCLUDED.first_episode_year, media_items.first_episode_year), \
                     studios = CASE WHEN jsonb_array_length(EXCLUDED.studios) > 0 THEN EXCLUDED.studios ELSE media_items.studios END, \
                     tags = CASE WHEN jsonb_array_length(EXCLUDED.tags) > 0 THEN EXCLUDED.tags ELSE media_items.tags END, \
                     detail = COALESCE(EXCLUDED.detail, media_items.detail), \
                     updated_at = now() \
                 RETURNING id",
            )
            .bind(&key)
            .bind(&item.kind)
            .bind(&item.title)
            .bind(item.year)
            .bind(&item.overview)
            .bind(&item.poster_path)
            .bind(item.runtime_seconds)
            .bind(serde_json::to_string(&item.genres).unwrap_or_else(|_| "[]".to_string()))
            .bind(&item.official_rating)
            .bind(&item.premiere_date)
            .bind(item.first_episode_year)
            .bind(serde_json::to_string(&item.studios).unwrap_or_else(|_| "[]".to_string()))
            .bind(serde_json::to_string(&item.tags).unwrap_or_else(|_| "[]".to_string()))
            .bind(&item.detail)
            .fetch_one(&mut *tx)
            .await?;
            let item_id: i64 = row.try_get("id")?;
            sqlx::query(
                "INSERT INTO media_item_sources \
                 (item_id, source_kind, connection_id, library, remote_id, poster_path, updated_at) \
                 VALUES ($1, $2, $3, $4, $5, $6, now()) \
                 ON CONFLICT (item_id, source_kind, connection_id, library, remote_id) DO UPDATE SET \
                     poster_path = EXCLUDED.poster_path, updated_at = now()",
            )
            .bind(item_id)
            .bind(source_kind)
            .bind(connection_id)
            .bind(library)
            .bind(&item.remote_id)
            .bind(&item.poster_path)
            .execute(&mut *tx)
            .await?;

            // The playable files this item's source reported.
            for file in &item.files {
                let path = file.path.trim();
                if path.is_empty() {
                    continue;
                }
                sqlx::query(
                    "INSERT INTO media_files \
                     (item_id, source_kind, connection_id, library, remote_id, path, container, \
                      size_bytes, runtime_seconds, streams, chapters, updated_at) \
                     VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb, $11::jsonb, now()) \
                     ON CONFLICT (item_id, source_kind, connection_id, library, path) DO UPDATE SET \
                         remote_id = EXCLUDED.remote_id, container = EXCLUDED.container, \
                         size_bytes = EXCLUDED.size_bytes, runtime_seconds = EXCLUDED.runtime_seconds, \
                         streams = EXCLUDED.streams, chapters = EXCLUDED.chapters, updated_at = now()",
                )
                .bind(item_id)
                .bind(source_kind)
                .bind(connection_id)
                .bind(library)
                .bind(&file.remote_id)
                .bind(path)
                .bind(&file.container)
                .bind(file.size_bytes)
                .bind(file.runtime_seconds)
                .bind(file.streams.as_ref().map(|value| value.to_string()))
                .bind(file.chapters.as_ref().map(|value| value.to_string()))
                .execute(&mut *tx)
                .await?;
            }

            // Cast and crew: upsert each person by name, then the credit link.
            for credit in &item.people {
                let name = credit.name.trim();
                if name.is_empty() {
                    continue;
                }
                let person = sqlx::query(
                    "INSERT INTO media_people (name) VALUES ($1) \
                     ON CONFLICT (name) DO UPDATE SET name = EXCLUDED.name \
                     RETURNING id",
                )
                .bind(name)
                .fetch_one(&mut *tx)
                .await?;
                let person_id: i64 = person.try_get("id")?;
                sqlx::query(
                    "INSERT INTO media_item_people \
                     (item_id, person_id, role_type, role, character, sort_order) \
                     VALUES ($1, $2, $3, $4, $5, $6) \
                     ON CONFLICT (item_id, person_id, role_type, role) DO UPDATE SET \
                         character = EXCLUDED.character, sort_order = EXCLUDED.sort_order",
                )
                .bind(item_id)
                .bind(person_id)
                .bind(&credit.role_type)
                .bind(&credit.role)
                .bind(&credit.character)
                .bind(item.people.iter().position(|c| c.name == credit.name).unwrap_or(0) as i32)
                .execute(&mut *tx)
                .await?;
            }
        }
        // A media entry with no sources is no longer provided by anything.
        sqlx::query(
            "DELETE FROM media_items \
             WHERE NOT EXISTS (SELECT 1 FROM media_item_sources s WHERE s.item_id = media_items.id)",
        )
        .execute(&mut *tx)
        .await?;
        tx.commit().await?;
        Ok(items.len())
    }

    pub async fn media_clear_connection(
        pool: &PgPool,
        connection_id: i64,
    ) -> Result<(), StoreError> {
        sqlx::query("DELETE FROM media_item_sources WHERE connection_id = $1")
            .bind(connection_id)
            .execute(pool)
            .await?;
        sqlx::query("DELETE FROM media_files WHERE connection_id = $1")
            .bind(connection_id)
            .execute(pool)
            .await?;
        sqlx::query(
            "DELETE FROM media_items \
             WHERE NOT EXISTS (SELECT 1 FROM media_item_sources s WHERE s.item_id = media_items.id)",
        )
        .execute(pool)
        .await?;
        Ok(())
    }

    pub async fn media_files_for(
        pool: &PgPool,
        match_key: &str,
    ) -> Result<Vec<serde_json::Value>, StoreError> {
        let rows = sqlx::query(
            "SELECT f.source_kind, f.path, f.runtime_seconds \
             FROM media_files f JOIN media_items m ON m.id = f.item_id \
             WHERE m.match_key = $1 ORDER BY f.source_kind, f.path",
        )
        .bind(match_key)
        .fetch_all(pool)
        .await?;
        Ok(rows
            .into_iter()
            .map(|row| {
                serde_json::json!({
                    "source_kind": row.try_get::<String, _>("source_kind").unwrap_or_default(),
                    "path": row.try_get::<String, _>("path").unwrap_or_default(),
                    "runtime_seconds": row.try_get::<Option<i64>, _>("runtime_seconds").ok().flatten(),
                })
            })
            .collect())
    }

    pub async fn media_list(pool: &PgPool) -> Result<Vec<serde_json::Value>, StoreError> {
        let rows = sqlx::query(
            "SELECT m.match_key, m.kind, m.title, m.year, m.overview, m.poster_path, \
                    m.runtime_seconds, m.genres, m.official_rating, m.premiere_date, \
                    m.first_episode_year, m.studios, m.tags, m.detail, \
                    (SELECT COALESCE(json_agg(json_build_object(
                        'name', p.name, 'role_type', ip.role_type, 'role', ip.role,
                        'character', ip.character, 'image_path', p.image_path)
                        ORDER BY ip.sort_order), '[]'::json)
                     FROM media_item_people ip JOIN media_people p ON p.id = ip.person_id
                     WHERE ip.item_id = m.id) AS people,
                    (SELECT COALESCE(json_agg(json_build_object(
                        'name', p.name, 'role_type', ip.role_type, 'role', ip.role,
                        'character', ip.character, 'image_path', p.image_path)
                        ORDER BY ip.sort_order), '[]'::json)
                     FROM media_item_people ip JOIN media_people p ON p.id = ip.person_id
                     WHERE ip.item_id = m.id) AS people,
                    COALESCE(json_agg(json_build_object( \
                        'source_kind', s.source_kind, 'connection_id', s.connection_id, \
                        'library', s.library, 'remote_id', s.remote_id, \
                        'poster_path', s.poster_path) \
                        ORDER BY s.source_kind, s.connection_id) \
                        FILTER (WHERE s.item_id IS NOT NULL), '[]'::json) AS sources \
             FROM media_items m \
             LEFT JOIN media_item_sources s ON s.item_id = m.id \
             GROUP BY m.id \
             ORDER BY m.kind, m.title",
        )
        .fetch_all(pool)
        .await?;
        let mut items = Vec::new();
        for row in rows {
            let sources: serde_json::Value =
                row.try_get::<Json<serde_json::Value>, _>("sources")?.0;
            items.push(serde_json::json!({
                "match_key": row.try_get::<String, _>("match_key")?,
                "kind": row.try_get::<String, _>("kind")?,
                "title": row.try_get::<String, _>("title")?,
                "year": row.try_get::<Option<i32>, _>("year")?,
                "overview": row.try_get::<Option<String>, _>("overview")?,
                "poster_path": row.try_get::<Option<String>, _>("poster_path")?,
                "runtime_seconds": row.try_get::<Option<i64>, _>("runtime_seconds")?,
                "genres": json_array(row.try_get::<Json<serde_json::Value>, _>("genres")?.0),
                "official_rating": row.try_get::<Option<String>, _>("official_rating")?,
                "premiere_date": row.try_get::<Option<String>, _>("premiere_date")?,
                "first_episode_year": row.try_get::<Option<i32>, _>("first_episode_year")?,
                "studios": json_array(row.try_get::<Json<serde_json::Value>, _>("studios")?.0),
                "tags": json_array(row.try_get::<Json<serde_json::Value>, _>("tags")?.0),
                "detail": row.try_get::<Option<Json<serde_json::Value>>, _>("detail")?.map(|value| value.0),
                "people": row.try_get::<Json<serde_json::Value>, _>("people")?.0,
                "sources": sources,
            }));
        }
        Ok(items)
    }

    pub async fn media_counts(pool: &PgPool) -> Result<Vec<(String, i64)>, StoreError> {
        let rows = sqlx::query(
            "SELECT kind, count(*)::int8 AS c FROM media_items GROUP BY kind ORDER BY kind",
        )
        .fetch_all(pool)
        .await?;
        Ok(rows
            .iter()
            .map(|row| (row.try_get::<String, _>("kind").unwrap_or_default(), row.try_get::<i64, _>("c").unwrap_or(0)))
            .collect())
    }

    /// One page of the catalog, by kinds (empty = all) + optional title query.
    pub async fn media_page(
        pool: &PgPool,
        kinds: &[String],
        offset: usize,
        limit: Option<usize>,
        query: &str,
    ) -> Result<(Vec<serde_json::Value>, i64), StoreError> {
        let q = query.trim();
        let kind_sql = if kinds.is_empty() {
            String::new()
        } else {
            let list = kinds
                .iter()
                .map(|kind| format!("'{}'", kind.as_str()))
                .collect::<Vec<_>>()
                .join(",");
            format!(" WHERE m.kind IN ({list})")
        };
        if q.contains('\'') {
            // The title filter is bound as a parameter below; this guard only
            // keeps the literal clause building honest.
            return Err(StoreError::Invalid("invalid search term"));
        }
        let q_sql = if q.is_empty() {
            String::new()
        } else if kinds.is_empty() {
            " WHERE m.title ILIKE $1".to_string()
        } else {
            " AND m.title ILIKE $1".to_string()
        };
        let count_sql = format!("SELECT count(*)::int8 AS c FROM media_items m{kind_sql}{q_sql}");
        let mut count_query = sqlx::query(&count_sql);
        if !q.is_empty() {
            count_query = count_query.bind(format!("%{q}%"));
        }
        let total: i64 = count_query.fetch_one(pool).await?.try_get("c")?;

        let mut select = "SELECT m.match_key, m.kind, m.title, m.year, m.overview, m.poster_path, \
                 m.runtime_seconds, m.genres, m.official_rating, m.premiere_date, \
                 m.first_episode_year, m.studios, m.tags, m.detail, \
                    (SELECT COALESCE(json_agg(json_build_object(
                        'name', p.name, 'role_type', ip.role_type, 'role', ip.role,
                        'character', ip.character, 'image_path', p.image_path)
                        ORDER BY ip.sort_order), '[]'::json)
                     FROM media_item_people ip JOIN media_people p ON p.id = ip.person_id
                     WHERE ip.item_id = m.id) AS people,
                 COALESCE(json_agg(json_build_object( \
                     'source_kind', s.source_kind, 'connection_id', s.connection_id, \
                     'library', s.library, 'remote_id', s.remote_id, \
                     'poster_path', s.poster_path) \
                     ORDER BY s.source_kind, s.connection_id) \
                     FILTER (WHERE s.item_id IS NOT NULL), '[]'::json) AS sources \
              FROM media_items m \
              LEFT JOIN media_item_sources s ON s.item_id = m.id"
            .to_string();
        select.push_str(&kind_sql);
        select.push_str(&q_sql);
        select.push_str(" GROUP BY m.id ORDER BY m.kind, m.title");
        if let Some(limit) = limit {
            select.push_str(&format!(" LIMIT {limit}"));
        }
        select.push_str(&format!(" OFFSET {offset}"));

        let mut page_query = sqlx::query(&select);
        if !q.is_empty() {
            page_query = page_query.bind(format!("%{q}%"));
        }
        let rows = page_query.fetch_all(pool).await?;
        let mut items = Vec::new();
        for row in rows {
            let sources: Json<serde_json::Value> = row.try_get("sources")?;
            items.push(serde_json::json!({
                "match_key": row.try_get::<String, _>("match_key")?,
                "kind": row.try_get::<String, _>("kind")?,
                "title": row.try_get::<String, _>("title")?,
                "year": row.try_get::<Option<i32>, _>("year")?,
                "overview": row.try_get::<Option<String>, _>("overview")?,
                "poster_path": row.try_get::<Option<String>, _>("poster_path")?,
                "runtime_seconds": row.try_get::<Option<i64>, _>("runtime_seconds")?,
                "genres": json_array(row.try_get::<Json<serde_json::Value>, _>("genres")?.0),
                "official_rating": row.try_get::<Option<String>, _>("official_rating")?,
                "premiere_date": row.try_get::<Option<String>, _>("premiere_date")?,
                "first_episode_year": row.try_get::<Option<i32>, _>("first_episode_year")?,
                "studios": json_array(row.try_get::<Json<serde_json::Value>, _>("studios")?.0),
                "tags": json_array(row.try_get::<Json<serde_json::Value>, _>("tags")?.0),
                "detail": row.try_get::<Option<Json<serde_json::Value>>, _>("detail")?.map(|value| value.0),
                "people": row.try_get::<Json<serde_json::Value>, _>("people")?.0,
                "sources": sources.0,
            }));
        }
        Ok((items, total))
    }

    /// Drop every table this plugin created and delete its key/value rows.
    pub async fn drop_plugin_data(pool: &PgPool, namespace: &str) -> Result<u64, StoreError> {
        let prefix = sanitise_table_prefix(namespace);
        let rows = sqlx::query(
            "SELECT tablename FROM pg_tables \
             WHERE schemaname = 'public' AND tablename LIKE $1",
        )
        .bind(format!("{prefix}%"))
        .fetch_all(pool)
        .await?;
        let mut dropped = 0u64;
        for row in rows {
            let name: String = row.try_get("tablename")?;
            sqlx::query(&format!("DROP TABLE IF EXISTS {name}"))
                .execute(pool)
                .await?;
            dropped += 1;
        }
        let removed = sqlx::query("DELETE FROM plugin_kv WHERE namespace = $1")
            .bind(namespace)
            .execute(pool)
            .await?;
        Ok(dropped + removed.rows_affected())
    }

    /// The pre-plugin `ai_settings` row, if the old schema is still around.
    pub async fn read_legacy_ai(pool: &PgPool) -> Result<Option<serde_json::Value>, StoreError> {
        let legacy: i64 = sqlx::query(
            "SELECT count(*) FROM information_schema.tables \
             WHERE table_schema = 'public' AND table_name = 'ai_settings'",
        )
        .fetch_one(pool)
        .await?
        .try_get(0)?;
        if legacy == 0 {
            return Ok(None);
        }
        match sqlx::query("SELECT config FROM ai_settings WHERE id = 1")
            .fetch_optional(pool)
            .await?
        {
            Some(row) => Ok(Some(row.try_get::<Json<serde_json::Value>, _>("config")?.0)),
            None => Ok(None),
        }
    }

    pub async fn remove_legacy_ai(pool: &PgPool) -> Result<(), StoreError> {
        sqlx::query("DROP TABLE IF EXISTS ai_settings")
            .execute(pool)
            .await?;
        Ok(())
    }

    fn channel_columns() -> &'static str {
        "SELECT id, number, name, description, ai_prompt, enabled, transcode, watermark, created_at, updated_at \
         FROM channels"
    }

    fn row_to_channel(row: &sqlx::postgres::PgRow) -> Result<Channel, StoreError> {
        Ok(Channel {
            id: row.try_get("id")?,
            number: row.try_get::<f64, _>("number")?,
            name: row.try_get("name")?,
            description: row.try_get("description")?,
            ai_prompt: row.try_get("ai_prompt")?,
            enabled: row.try_get("enabled")?,
            watermark: row
                .try_get::<Json<Option<crate::model::Watermark>>, _>("watermark")?
                .0,
            transcode: row.try_get::<Json<serde_json::Value>, _>("transcode")?.0,
            created_at: row.try_get("created_at")?,
            updated_at: row.try_get("updated_at")?,
        })
    }

    async fn insert(pool: &PgPool, channel: &Channel) -> Result<(), StoreError> {
        sqlx::query(
            "INSERT INTO channels
                (id, number, name, description, ai_prompt, enabled, transcode, watermark, created_at, updated_at)
             VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)",
        )
        .bind(channel.id)
        .bind(channel.number)
        .bind(&channel.name)
        .bind(&channel.description)
        .bind(&channel.ai_prompt)
        .bind(channel.enabled)
        .bind(Json(channel.transcode.clone()))
        .bind(Json(channel.watermark.clone()))
        .bind(channel.created_at)
        .bind(channel.updated_at)
        .execute(pool)
        .await?;
        Ok(())
    }

    async fn update_row(pool: &PgPool, channel: &Channel) -> Result<(), StoreError> {
        sqlx::query(
            "UPDATE channels
             SET number = $2, name = $3, description = $4, ai_prompt = $5, enabled = $6,
                 transcode = $7, watermark = $8, updated_at = $9
             WHERE id = $1",
        )
        .bind(channel.id)
        .bind(channel.number)
        .bind(&channel.name)
        .bind(&channel.description)
        .bind(&channel.ai_prompt)
        .bind(channel.enabled)
        .bind(Json(channel.transcode.clone()))
        .bind(Json(channel.watermark.clone()))
        .bind(channel.updated_at)
        .execute(pool)
        .await?;
        Ok(())
    }

    async fn reserve(pool: &PgPool, number: f64, ignore: Option<Uuid>) -> Result<(), StoreError> {
        let count: i64 = match ignore {
            Some(id) => sqlx::query("SELECT count(*) FROM channels WHERE number = $1 AND id <> $2")
                .bind(number as i32)
                .bind(id)
                .fetch_one(pool)
                .await?
                .try_get(0)?,
            None => sqlx::query("SELECT count(*) FROM channels WHERE number = $1")
                .bind(number as i32)
                .fetch_one(pool)
                .await?
                .try_get(0)?,
        };
        if count > 0 {
            return Err(StoreError::DuplicateNumber(number));
        }
        Ok(())
    }
}

/// Validation shared by both backends: a channel's name and number must make
/// sense before it is checked against the ones already stored.
fn checked_new(mut input: NewChannel) -> Result<NewChannel, StoreError> {
    let name = input.name.trim();
    if name.is_empty() {
        return Err(StoreError::Invalid("channel name cannot be empty"));
    }
    if input.number == 0.0 {
        return Err(StoreError::Invalid("channel number must be at least 1"));
    }
    input.name = name.to_string();
    input.description = input.description.trim().to_string();
    Ok(input)
}

/// Write a file that may hold secrets. Plugin storage files get mode `0600`:
/// never world-readable even for the instant between temp file and rename.
fn write_private(path: &Path, contents: &str) -> std::io::Result<()> {
    use std::io::Write;

    let tmp = path.with_extension("json.tmp");
    let mut options = fs::OpenOptions::new();
    options.write(true).create(true).truncate(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let mut file = options.open(&tmp)?;
    file.write_all(contents.as_bytes())?;
    drop(file);
    fs::rename(&tmp, path)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::NewChannel;
    use serde_json::json;

    /// A plugin id becomes a dotted name with underscores, and only plain
    /// identifiers are allowed as the plugin's own table name.
    #[test]
    fn plugin_table_names_are_namespaced_and_safe() {
        assert_eq!(
            sanitise_table_prefix("com.channelflow.ersatztv"),
            "cf_com_channelflow_ersatztv"
        );
        assert!(is_identifier("audit"));
        assert!(is_identifier("channel_overrides_2"));
        assert!(!is_identifier("audit; drop table channels"));
        assert!(!is_identifier(""));
    }

    /// The file backend keeps the round trip, and can be deleted.
    #[tokio::test]
    async fn file_backend_plugin_storage_round_trip() {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("channelflow-kv-{stamp}"));
        let store = Store::open(&dir).expect("open file store");

        let value = json!({ "providers": [{ "id": "p1", "name": "One", "priority": 1 }] });
        store
            .plugin_set("com.channelflow.ai", "providers", &value)
            .await
            .expect("set");
        assert_eq!(
            store
                .plugin_get("com.channelflow.ai", "providers")
                .await
                .expect("get"),
            Some(value)
        );

        // A different namespace never sees this key.
        assert_eq!(
            store
                .plugin_get("com.channelflow.ersatztv", "providers")
                .await
                .expect("get other"),
            None
        );

        store
            .plugin_delete("com.channelflow.ai", "providers")
            .await
            .expect("delete");
        assert!(store
            .plugin_get("com.channelflow.ai", "providers")
            .await
            .expect("get after delete")
            .is_none());

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// A legacy `ai.json` is moved into the AI plugin's own storage exactly
    /// once, and the file is removed.
    #[tokio::test]
    async fn legacy_ai_migrates_into_plugin_storage() {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("channelflow-ai-migrate-{stamp}"));
        std::fs::create_dir_all(&dir).expect("dir");
        let legacy = json!({
            "base_url": "https://api.openai.com/v1",
            "api_key": "sk-secret",
            "chat_model": "gpt-4o-mini",
            "tts_model": "tts-1",
            "voice": "nova"
        });
        std::fs::write(dir.join("ai.json"), serde_json::to_string(&legacy).unwrap()).unwrap();

        let store = Store::open(&dir).expect("open file store");
        store.upgrade_legacy_ai().await.expect("migrate");
        let migrated = store
            .plugin_get("com.channelflow.ai", "providers")
            .await
            .expect("get");
        assert_eq!(migrated, Some(legacy), "document carried over whole");
        assert!(
            !dir.join("ai.json").exists(),
            "legacy file is removed after the move"
        );

        // Nothing to do a second time.
        store.upgrade_legacy_ai().await.expect("second migrate");
        store
            .plugin_delete("com.channelflow.ai", "providers")
            .await
            .expect("cleanup");
        std::fs::remove_file(dir.join("ai.json")).ok();
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// The LLM provider handed to plugins is the lowest-priority enabled
    /// provider on the AI page that can write text — never a TTS-only one.
    #[tokio::test]
    async fn llm_provider_picks_the_best_chat_provider() {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("channelflow-llm-{stamp}"));
        std::fs::create_dir_all(&dir).expect("dir");
        let store = Store::open(&dir).expect("open file store");

        // Nothing configured yet.
        assert!(store.core_data(reqwest::Client::new()).llm_provider().await.expect("none").is_none());

        store
            .plugin_set(
                "com.channelflow.ai",
                "providers",
                &json!({ "providers": [
                    // A TTS-only provider must be skipped even at priority 0.
                    { "id": "t", "name": "tts", "priority": 0, "enabled": true,
                      "tts_only": true, "base_url": "https://tts.example/v1",
                      "chat_model": "", "tts_model": "kokoro" },
                    // A chat provider with a higher number must lose to the
                    // lower-numbered one.
                    { "id": "v", "name": "venice", "priority": 3, "enabled": true,
                      "tts_only": false, "base_url": "https://api.example/v1/",
                      "api_key": "k3", "chat_model": "big-model" },
                    { "id": "o", "name": "ollama", "priority": 1, "enabled": true,
                      "tts_only": false, "base_url": "https://ollama.example/v1",
                      "api_key": "", "chat_model": "llama3" }
                ] }),
            )
            .await
            .expect("set");

        let provider = store
            .core_data(reqwest::Client::new())
            .llm_provider()
            .await
            .expect("read")
            .expect("a chat provider");
        assert_eq!(provider.base_url, "https://ollama.example/v1");
        assert_eq!(provider.chat_model, "llama3");
        assert!(provider.api_key.is_empty());

        // Disabling the winner falls through to the next by priority.
        store
            .plugin_set(
                "com.channelflow.ai",
                "providers",
                &json!({ "providers": [
                    { "id": "o", "name": "ollama", "priority": 1, "enabled": false,
                      "tts_only": false, "base_url": "https://ollama.example/v1",
                      "chat_model": "llama3" },
                    { "id": "v", "name": "venice", "priority": 2, "enabled": true,
                      "tts_only": false, "base_url": "https://api.example/v1/",
                      "api_key": "k", "chat_model": "big-model" }
                ] }),
            )
            .await
            .expect("set 2");
        let provider = store
            .core_data(reqwest::Client::new())
            .llm_provider()
            .await
            .expect("read 2")
            .expect("the fallback");
        assert_eq!(provider.base_url, "https://api.example/v1", "trailing slash trimmed");
        assert_eq!(provider.api_key, "k");

        store
            .plugin_delete("com.channelflow.ai", "providers")
            .await
            .expect("cleanup");
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// The TTS provider handed to plugins prefers a `tts_only` provider over
    /// one that merely has a `tts_model`, and falls back to the latter when no
    /// dedicated one is enabled.
    #[tokio::test]
    async fn tts_provider_prefers_a_dedicated_tts_provider() {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("channelflow-tts-{stamp}"));
        std::fs::create_dir_all(&dir).expect("dir");
        let store = Store::open(&dir).expect("open file store");

        assert!(store.core_data(reqwest::Client::new()).tts_provider().await.expect("none").is_none());

        // The provider set the AI page holds live: a chat provider first, a
        // dedicated TTS provider second, and a chat provider that also names a
        // tts_model third.
        store
            .plugin_set(
                "com.channelflow.ai",
                "providers",
                &json!({ "providers": [
                    { "id": "o", "name": "ollama", "priority": 1, "enabled": true,
                      "tts_only": false, "base_url": "https://ollama.example/v1",
                      "chat_model": "llama3", "tts_model": "" },
                    { "id": "t", "name": "tts", "priority": 2, "enabled": true,
                      "tts_only": true, "base_url": "https://tts.example/v1/",
                      "api_key": "k", "tts_model": "kokoro", "voice": "af_sarah" },
                    { "id": "v", "name": "venice", "priority": 3, "enabled": true,
                      "tts_only": false, "base_url": "https://api.example/v1",
                      "tts_model": "tts-kokoro", "voice": "af_sarah" }
                ] }),
            )
            .await
            .expect("set");

        let provider = store
            .core_data(reqwest::Client::new())
            .tts_provider()
            .await
            .expect("read")
            .expect("a TTS provider");
        assert_eq!(provider.base_url, "https://tts.example/v1", "trailing slash trimmed");
        assert_eq!(provider.tts_model, "kokoro");
        assert_eq!(provider.voice, "af_sarah");
        assert_eq!(provider.api_key, "k");

        // With the dedicated provider disabled the chat provider that names a
        // tts_model takes over.
        store
            .plugin_set(
                "com.channelflow.ai",
                "providers",
                &json!({ "providers": [
                    { "id": "o", "name": "ollama", "priority": 1, "enabled": true,
                      "tts_only": false, "base_url": "https://ollama.example/v1",
                      "chat_model": "llama3", "tts_model": "" },
                    { "id": "t", "name": "tts", "priority": 2, "enabled": false,
                      "tts_only": true, "base_url": "https://tts.example/v1",
                      "tts_model": "kokoro" },
                    { "id": "v", "name": "venice", "priority": 3, "enabled": true,
                      "tts_only": false, "base_url": "https://api.example/v1",
                      "tts_model": "tts-kokoro", "voice": "af_sarah" }
                ] }),
            )
            .await
            .expect("set 2");
        let provider = store
            .core_data(reqwest::Client::new())
            .tts_provider()
            .await
            .expect("read 2")
            .expect("the chat provider with a tts_model");
        assert_eq!(provider.base_url, "https://api.example/v1");
        assert_eq!(provider.tts_model, "tts-kokoro");

        store
            .plugin_delete("com.channelflow.ai", "providers")
            .await
            .expect("cleanup");
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// A legacy `transcode.json` plus per-channel patches move into the
    /// ErsatzTV plugin's storage, and the channel patches are cleared.
    #[tokio::test]
    async fn legacy_transcode_migrates_into_plugin_storage() {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("channelflow-tc-migrate-{stamp}"));
        std::fs::create_dir_all(dir.join("channels")).expect("dir");

        std::fs::write(
            dir.join("transcode.json"),
            serde_json::to_string_pretty(&json!({
                "normalization": { "video": { "bit_depth": 10 } }
            }))
            .unwrap(),
        )
        .unwrap();

        // A channel whose document still carries a transcode patch.
        let store = Store::open(&dir).expect("open file store");
        let channel = store
            .create(NewChannel {
                number: 1.0,
                name: "Migrating".to_string(),
                description: String::new(),
                enabled: true,
                watermark: None,
                ai_prompt: String::new(),
            })
            .await
            .expect("create");
        let id = channel.id.to_string();
        store
            .set_channel_transcode(
                channel.id,
                json!({ "normalization": { "video": { "bitrate_kbps": 2500 } } }),
            )
            .await
            .expect("override");

        store.upgrade_legacy_transcode().await.expect("migrate");

        let defaults = store
            .plugin_get("com.channelflow.ersatztv", "defaults")
            .await
            .expect("defaults");
        assert_eq!(
            defaults.unwrap()["normalization"]["video"]["bit_depth"],
            json!(10),
            "instance defaults carried over whole"
        );
        let overrides = store
            .plugin_get("com.channelflow.ersatztv", "overrides")
            .await
            .expect("overrides")
            .unwrap();
        assert_eq!(
            overrides[&id]["normalization"]["video"]["bitrate_kbps"],
            json!(2500),
            "the channel's patch moved into the plugin's map"
        );
        assert!(
            !dir.join("transcode.json").exists(),
            "the legacy defaults file is removed"
        );
        assert_eq!(
            store.get(channel.id).await.unwrap().transcode,
            json!({}),
            "the channel's own copy is cleared"
        );
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// The Postgres backend end to end. Skipped unless `TEST_DATABASE_URL` is
    /// set, so `cargo test` needs no database by default.
    #[tokio::test]
    async fn postgres_backend_round_trip() {
        let url = match std::env::var("TEST_DATABASE_URL") {
            Ok(url) if !url.trim().is_empty() => url,
            _ => {
                eprintln!("skipped: TEST_DATABASE_URL is not set");
                return;
            }
        };

        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("channelflow-store-{stamp}"));
        let store = Store::open_postgres(&url, &dir)
            .await
            .expect("open postgres store");

        let created = store
            .create(NewChannel {
                number: 1.0,
                name: "One".to_string(),
                description: String::new(),
                enabled: true,
                watermark: None,
                ai_prompt: String::new(),
            })
            .await
            .expect("create");
        assert_eq!(store.list().await.expect("list").len(), 1);

        let updated = store
            .update(
                created.id,
                UpdateChannel {
                    number: Some(2.0),
                    name: Some("Two".to_string()),
                    description: None,
                    enabled: None,
                    ai_prompt: None,
                    watermark: None,
                },
            )
            .await
            .expect("update");
        assert_eq!(updated.number, 2.0);
        assert_eq!(updated.name, "Two");

        let duplicate = store
            .create(NewChannel {
                number: 2.0,
                name: "Clash".to_string(),
                description: String::new(),
                enabled: true,
                watermark: None,
                ai_prompt: String::new(),
            })
            .await;
        assert!(matches!(
            duplicate,
            Err(StoreError::DuplicateNumber(number)) if number == 2.0
        ));

        let overridden = store
            .set_channel_transcode(created.id, json!({ "normalization": { "video": { "bitrate_kbps": 2500 } } }))
            .await
            .expect("set overrides");
        assert_eq!(
            overridden.transcode["normalization"]["video"]["bitrate_kbps"],
            json!(2500)
        );

        store.delete(created.id).await.expect("delete");
        assert!(store.get(created.id).await.is_err(), "deleted channel is gone");

        // Plugin storage also round-trips.
        let provider = json!({ "providers": [{ "id": "p1", "name": "One", "priority": 1 }] });
        store
            .plugin_set("com.channelflow.ai", "providers", &provider)
            .await
            .expect("set");
        assert_eq!(
            store
                .plugin_get("com.channelflow.ai", "providers")
                .await
                .expect("get"),
            Some(provider)
        );
        store
            .plugin_delete("com.channelflow.ai", "providers")
            .await
            .expect("delete");

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// A fresh temp directory per test.
    fn scratch(tag: &str) -> std::path::PathBuf {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("channelflow-{tag}-{stamp}"));
        std::fs::create_dir_all(&dir).expect("dir");
        dir
    }

    /// A media source's reported files land in the core catalog, `file_for`
    /// resolves them from there, and a filter narrows the pool.
    #[tokio::test]
    async fn catalog_carries_reported_files_and_filters_the_pool() {
        use channelflow_plugin_api::media::CatalogFile;

        let dir = scratch("catalog");
        let store = Store::open(&dir).expect("open file store");
        let items = vec![
            CatalogItem::new("movie", "r1", "The Quiet Film")
                .year(Some(1999))
                .runtime_seconds(Some(5400))
                .genres(vec!["Drama".to_string()])
                .official_rating(Some("PG-13".to_string()))
                .files(vec![
                    CatalogFile::new("/media/quiet.mkv").runtime_seconds(Some(5400))
                ]),
            CatalogItem::new("movie", "r2", "Loud Action")
                .year(Some(2020))
                .official_rating(Some("R".to_string()))
                .files(vec![CatalogFile::new("/media/loud.mkv")]),
        ];
        store
            .media_replace_library(1, "local", &items)
            .await
            .expect("report");

        let core = store.core_data(reqwest::Client::new());
        let all = core
            .catalog(CatalogFilter::default())
            .await
            .expect("catalog");
        assert_eq!(all.len(), 2);

        // A rating ceiling drops the R-rated film.
        let capped = core
            .catalog(CatalogFilter {
                kind: Some("movie".to_string()),
                rating_max: Some("PG-13".to_string()),
                ..Default::default()
            })
            .await
            .expect("catalog");
        assert_eq!(capped.len(), 1);
        assert_eq!(capped[0].title, "The Quiet Film");
        assert_eq!(capped[0].runtime_ms, Some(5_400_000));

        // A limit caps the pool.
        let limited = core
            .catalog(CatalogFilter {
                limit: Some(1),
                ..Default::default()
            })
            .await
            .expect("catalog");
        assert_eq!(limited.len(), 1);

        // The reported file resolves through the core, not the plugin.
        let key = capped[0].match_key.clone();
        let picked = core
            .catalog(CatalogFilter {
                match_keys: vec![key.clone()],
                ..Default::default()
            })
            .await
            .expect("catalog");
        assert_eq!(picked.len(), 1);
        let file = core.file_for(&key).await.expect("file").expect("a file");
        assert_eq!(file.path, "/media/quiet.mkv");
        // The source kind comes from the connection; this test reports without
        // one, so it is the generic fallback.
        assert_eq!(file.source_kind, "source");
        assert_eq!(file.runtime_ms, Some(5_400_000));

        // Clearing the connection drops its files with it.
        store.media_clear_connection(1).await.expect("clear");
        assert!(core.file_for(&key).await.expect("file").is_none());

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// `lineup` hands a channel's saved roster back as JSON.
    #[tokio::test]
    async fn lineup_reads_the_saved_roster() {
        let dir = scratch("lineup");
        let store = Store::open(&dir).expect("open file store");
        let id = Uuid::new_v4();
        store
            .lineup_set(id, &json!({ "slots": [], "overrides": [] }))
            .await
            .expect("set");
        let core = store.core_data(reqwest::Client::new());
        let value = core
            .lineup(&id.to_string())
            .await
            .expect("lineup")
            .expect("a lineup");
        assert_eq!(value["slots"], json!([]));
        assert!(core
            .lineup("not-a-uuid")
            .await
            .expect("none")
            .is_none());
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// A stub OpenAI-compatible endpoint that always answers the same way.
    async fn stub_chat(status: u16, body: serde_json::Value) -> String {
        use axum::routing::post;
        use axum::Router;
        let code = reqwest::StatusCode::from_u16(status).expect("status");
        let app = Router::new().route(
            "/chat/completions",
            post(move || {
                let body = body.clone();
                async move { (code, axum::Json(body)) }
            }),
        );
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
            .await
            .expect("bind");
        let addr = listener.local_addr().expect("addr");
        tokio::spawn(async move {
            let _ = axum::serve(listener, app).await;
        });
        format!("http://{addr}")
    }

    /// `chat` walks the providers in order, reports the one it used, and stops
    /// the walk on a request the provider rejected outright.
    #[tokio::test]
    async fn chat_walks_the_providers_and_stops_on_a_bad_request() {
        let dir = scratch("chat");
        let store = Store::open(&dir).expect("open file store");
        let ok_url = stub_chat(
            200,
            json!({ "choices": [ { "message": { "content": "  {\"blocks\":[]}  " } } ] }),
        )
        .await;
        let down_url = stub_chat(500, json!({ "error": { "message": "boom" } })).await;
        let bad_url = stub_chat(400, json!({ "error": { "message": "bad shape" } })).await;

        let request = channelflow_plugin_api::core::ChatRequest {
            messages: vec![channelflow_plugin_api::core::ChatMessage {
                role: "user".to_string(),
                content: "hi".to_string(),
            }],
            temperature: Some(0.2),
            json: true,
            model: None,
        };
        let core = store.core_data(reqwest::Client::new());

        // A 5xx is retryable: the walk moves on and the second provider answers.
        store
            .plugin_set(
                "com.channelflow.ai",
                "providers",
                &json!({ "providers": [
                    { "id": "down", "name": "Down", "priority": 1, "enabled": true,
                      "tts_only": false, "base_url": down_url, "chat_model": "m" },
                    { "id": "good", "name": "Good", "priority": 2, "enabled": true,
                      "tts_only": false, "base_url": ok_url, "chat_model": "m" }
                ] }),
            )
            .await
            .expect("set");
        let response = core.chat(request.clone()).await.expect("chat");
        assert_eq!(response.content.trim(), "{\"blocks\":[]}");
        assert_eq!(response.provider_id, "good");
        assert!(
            response.attempts.iter().any(|line| line.starts_with("Down:")),
            "the failed provider is reported: {:?}",
            response.attempts
        );

        // A 400 is fatal: the walk stops instead of trying the good provider.
        store
            .plugin_set(
                "com.channelflow.ai",
                "providers",
                &json!({ "providers": [
                    { "id": "bad", "name": "Bad", "priority": 1, "enabled": true,
                      "tts_only": false, "base_url": bad_url, "chat_model": "m" },
                    { "id": "good", "name": "Good", "priority": 2, "enabled": true,
                      "tts_only": false, "base_url": ok_url, "chat_model": "m" }
                ] }),
            )
            .await
            .expect("set 2");
        assert!(
            core.chat(request).await.is_err(),
            "a rejected request stops the walk"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }
}