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
use channelflow_plugin_api::core::{CoreChannel, CoreData, CoreDataError};
use channelflow_plugin_api::database::{NoPluginDatabase, PluginDatabase, PluginDatabaseError};
use channelflow_plugin_api::storage::{PluginStorage, PluginStorageError};
use chrono::Utc;
use sqlx::postgres::{PgPool, PgPoolOptions};
use sqlx::types::Json;
use sqlx::Row;
use uuid::Uuid;

use crate::model::{Channel, NewChannel, UpdateChannel};
use crate::plugin::registry::PluginRegistry;

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

/// Storage failures, kept distinct from `anyhow` so the API layer can turn
/// `NotFound` into 404, `DuplicateNumber` into 409 and `Invalid` into 400
/// instead of reporting all of them as 500.
#[derive(Debug)]
pub enum StoreError {
    NotFound(Uuid),
    DuplicateNumber(u32),
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
    backend: Backend,
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
            backend: Backend::Files,
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
            backend: Backend::Postgres(pool),
        })
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
        match &self.backend {
            Backend::Files => file::set_channel_transcode(&self.root, id, overrides),
            Backend::Postgres(pool) => pg::set_channel_transcode(pool, id, overrides).await,
        }
    }

    /// Every channel, ordered by number then name.
    pub async fn list(&self) -> Result<Vec<Channel>, StoreError> {
        match &self.backend {
            Backend::Files => file::list(&self.root),
            Backend::Postgres(pool) => pg::list(pool).await,
        }
    }

    pub async fn get(&self, id: Uuid) -> Result<Channel, StoreError> {
        match &self.backend {
            Backend::Files => file::get(&self.root, id),
            Backend::Postgres(pool) => pg::get(pool, id).await,
        }
    }

    pub async fn create(&self, input: NewChannel) -> Result<Channel, StoreError> {
        match &self.backend {
            Backend::Files => file::create(&self.root, input),
            Backend::Postgres(pool) => pg::create(pool, input).await,
        }
    }

    pub async fn update(&self, id: Uuid, input: UpdateChannel) -> Result<Channel, StoreError> {
        match &self.backend {
            Backend::Files => file::update(&self.root, id, input),
            Backend::Postgres(pool) => pg::update(pool, id, input).await,
        }
    }

    pub async fn delete(&self, id: Uuid) -> Result<(), StoreError> {
        match &self.backend {
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
                number: 1,
                name: "ChannelFlow One".to_string(),
                description: "First channel — edit or replace me".to_string(),
                enabled: true,
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
        match &self.backend {
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
        match &self.backend {
            Backend::Files => file::plugin_set(&self.root, namespace, key, value),
            Backend::Postgres(pool) => pg::plugin_set(pool, namespace, key, value).await,
        }
    }

    pub async fn plugin_delete(
        &self,
        namespace: &str,
        key: &str,
    ) -> Result<(), StoreError> {
        match &self.backend {
            Backend::Files => file::plugin_delete(&self.root, namespace, key),
            Backend::Postgres(pool) => pg::plugin_delete(pool, namespace, key).await,
        }
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

    /// The read-only core-data handle handed to plugins that hold
    /// `api:core:read`.
    pub fn core_data(&self) -> StoreCoreData {
        StoreCoreData {
            store: self.clone(),
        }
    }

    /// A plugin's own Postgres tables (`storage:database`). On the file
    /// backend this is the no-database double, so a plugin can tell there is
    /// no Postgres and fall back to key/value storage.
    pub fn plugin_database(&self, namespace: &str) -> Arc<dyn PluginDatabase> {
        match &self.backend {
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

    /// Whether the registry has ever been written. A fresh install seeds the
    /// bundled plugins here; after that an empty registry stays empty, so
    /// removing every plugin is not undone by the next start.
    pub async fn plugin_registry_exists(&self) -> Result<bool, StoreError> {
        Ok(self
            .plugin_get(CORE_NAMESPACE, PLUGIN_REGISTRY_KEY)
            .await?
            .is_some())
    }

    pub async fn save_plugin_registry(&self, registry: &PluginRegistry) -> Result<(), StoreError> {
        let value = serde_json::to_value(registry)?;
        self.plugin_set(CORE_NAMESPACE, PLUGIN_REGISTRY_KEY, &value)
            .await
    }

    /// Erase everything a plugin stored: its key/value files or rows, and the
    /// tables it created. Returns how many tables/keys were dropped. This is
    /// what "remove and drop data" calls; "remove and keep data" skips it.
    pub async fn drop_plugin_data(&self, namespace: &str) -> Result<u64, StoreError> {
        match &self.backend {
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
        let legacy = match &self.backend {
            Backend::Files => file::read_legacy_ai(&self.root)?,
            Backend::Postgres(pool) => pg::read_legacy_ai(pool).await?,
        };
        let Some(value) = legacy else {
            return Ok(());
        };
        self.plugin_set(LEGACY_AI_NAMESPACE, LEGACY_AI_KEY, &value)
            .await?;
        tracing::info!(plugin = LEGACY_AI_NAMESPACE, "migrated legacy AI settings into plugin storage");
        match &self.backend {
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
            let legacy = match &self.backend {
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

        match &self.backend {
            Backend::Files => file::remove_legacy_transcode(&self.root)?,
            Backend::Postgres(pool) => pg::remove_legacy_transcode(pool).await?,
        }
        tracing::info!(plugin = LEGACY_TRANSCODE_NAMESPACE, "migrated legacy transcode settings into plugin storage");
        Ok(())
    }
}

/// The read-only view of channels handed to plugins that hold `api:core:read`.
#[derive(Clone)]
pub struct StoreCoreData {
    store: Store,
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

    async fn fetch(&self, sql: &str) -> Result<Vec<serde_json::Value>, PluginDatabaseError> {
        let wrapped = format!("SELECT COALESCE(json_agg(row_to_json(t)), '[]'::json) FROM ({sql}) t");
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
        channels.sort_by(|a, b| a.number.cmp(&b.number).then_with(|| a.name.cmp(&b.name)));
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
            if number == 0 {
                return Err(StoreError::Invalid("channel number must be at least 1"));
            }
            reserve(root, number, Some(id))?;
            channel.number = number;
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

    fn reserve(root: &Path, number: u32, ignore: Option<Uuid>) -> Result<(), StoreError> {
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
mod pg {
    use super::*;

    pub async fn ensure_schema(pool: &PgPool) -> Result<(), StoreError> {
        for statement in [
            "CREATE TABLE IF NOT EXISTS channels (
                id UUID PRIMARY KEY,
                number INTEGER NOT NULL UNIQUE,
                name TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                enabled BOOLEAN NOT NULL DEFAULT TRUE,
                transcode JSONB NOT NULL DEFAULT '{}'::jsonb,
                created_at TIMESTAMPTZ NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL
            )",
            "CREATE TABLE IF NOT EXISTS plugin_kv (
                namespace TEXT NOT NULL,
                key TEXT NOT NULL,
                value JSONB NOT NULL,
                PRIMARY KEY (namespace, key)
            )",
        ] {
            sqlx::query(statement).execute(pool).await?;
        }
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
            if number == 0 {
                return Err(StoreError::Invalid("channel number must be at least 1"));
            }
            reserve(pool, number, Some(id)).await?;
            channel.number = number;
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
        "SELECT id, number, name, description, enabled, transcode, created_at, updated_at \
         FROM channels"
    }

    fn row_to_channel(row: &sqlx::postgres::PgRow) -> Result<Channel, StoreError> {
        Ok(Channel {
            id: row.try_get("id")?,
            number: row.try_get::<i32, _>("number")? as u32,
            name: row.try_get("name")?,
            description: row.try_get("description")?,
            enabled: row.try_get("enabled")?,
            transcode: row.try_get::<Json<serde_json::Value>, _>("transcode")?.0,
            created_at: row.try_get("created_at")?,
            updated_at: row.try_get("updated_at")?,
        })
    }

    async fn insert(pool: &PgPool, channel: &Channel) -> Result<(), StoreError> {
        sqlx::query(
            "INSERT INTO channels
                (id, number, name, description, enabled, transcode, created_at, updated_at)
             VALUES ($1, $2, $3, $4, $5, $6, $7, $8)",
        )
        .bind(channel.id)
        .bind(channel.number as i32)
        .bind(&channel.name)
        .bind(&channel.description)
        .bind(channel.enabled)
        .bind(Json(channel.transcode.clone()))
        .bind(channel.created_at)
        .bind(channel.updated_at)
        .execute(pool)
        .await?;
        Ok(())
    }

    async fn update_row(pool: &PgPool, channel: &Channel) -> Result<(), StoreError> {
        sqlx::query(
            "UPDATE channels
             SET number = $2, name = $3, description = $4, enabled = $5,
                 transcode = $6, updated_at = $7
             WHERE id = $1",
        )
        .bind(channel.id)
        .bind(channel.number as i32)
        .bind(&channel.name)
        .bind(&channel.description)
        .bind(channel.enabled)
        .bind(Json(channel.transcode.clone()))
        .bind(channel.updated_at)
        .execute(pool)
        .await?;
        Ok(())
    }

    async fn reserve(pool: &PgPool, number: u32, ignore: Option<Uuid>) -> Result<(), StoreError> {
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
    if input.number == 0 {
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
                number: 1,
                name: "Migrating".to_string(),
                description: String::new(),
                enabled: true,
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
                number: 1,
                name: "One".to_string(),
                description: String::new(),
                enabled: true,
            })
            .await
            .expect("create");
        assert_eq!(store.list().await.expect("list").len(), 1);

        let updated = store
            .update(
                created.id,
                UpdateChannel {
                    number: Some(2),
                    name: Some("Two".to_string()),
                    description: None,
                    enabled: None,
                },
            )
            .await
            .expect("update");
        assert_eq!(updated.number, 2);
        assert_eq!(updated.name, "Two");

        let duplicate = store
            .create(NewChannel {
                number: 2,
                name: "Clash".to_string(),
                description: String::new(),
                enabled: true,
            })
            .await;
        assert!(matches!(duplicate, Err(StoreError::DuplicateNumber(2))));

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
}