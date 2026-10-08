//! Storage: channels, the instance transcode defaults, and the AI providers.
//!
//! Two backends sit behind one `Store`, so how the app persists its settings
//! does not change what the rest of it sees.
//!
//! * **Files** — the default, with zero setup. One JSON document per channel
//!   under `<config>/channels/`, plus `<config>/transcode.json` and
//!   `<config>/ai.json` (the last written `0600` because it holds keys).
//! * **Postgres** — used when `DATABASE_URL` is set. The same settings live in
//!   three tables. On first open against an empty database the config
//!   directory is read once and imported, so moving to Postgres keeps exactly
//!   what the files had; after that Postgres is the only source of truth and
//!   nothing is written to the directory.
//!
//! ErsatzTV next talks to a Postgres instance of its own. ChannelFlow needs no
//! shared tables with it, so the schema is deliberately small and ours: a
//! `channels` table, and one-row `transcode_settings` and `ai_settings`
//! tables holding the same JSON documents the file backend used.

use std::fs;
use std::path::{Path, PathBuf};

use chrono::Utc;
use sqlx::postgres::{PgPool, PgPoolOptions};
use sqlx::types::Json;
use sqlx::Row;
use uuid::Uuid;

use crate::ai::{AiConfig, AiError};
use crate::model::{Channel, NewChannel, UpdateChannel};
use crate::transcode::{TranscodeConfig, TranscodeError};

const TRANSCODE_FILE: &str = "transcode.json";
const AI_FILE: &str = "ai.json";

/// Storage failures, kept distinct from `anyhow` so the API layer can turn
/// `NotFound` into 404, `DuplicateNumber` into 409 and `Invalid` into 400
/// instead of reporting all of them as 500.
#[derive(Debug)]
pub enum StoreError {
    NotFound(Uuid),
    DuplicateNumber(u32),
    Invalid(&'static str),
    /// A transcode document — the defaults, or the body of a request — that
    /// next's schema would reject.
    Transcode(TranscodeError),
    /// An AI settings document that cannot be used as written.
    Ai(AiError),
    /// An AI provider id that is not in the list.
    ProviderNotFound(String),
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
            StoreError::Transcode(error) => write!(f, "{error}"),
            StoreError::Ai(error) => write!(f, "{error}"),
            StoreError::ProviderNotFound(id) => write!(f, "no AI provider with id {id}"),
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
    /// The file backend: `<config>/channels` plus the two settings files,
    /// seeded on first run so they are real, hand-editable documents.
    pub fn open(config: &Path) -> Result<Self, StoreError> {
        fs::create_dir_all(config.join("channels"))?;
        let store = Self {
            root: config.to_path_buf(),
            backend: Backend::Files,
        };
        seed_transcode_file(config)?;
        seed_ai_file(config)?;
        Ok(store)
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
        pg::ensure_transcode(&pool, config).await?;
        pg::ensure_ai(&pool, config).await?;
        pg::import_channels(&pool, config).await?;
        Ok(Self {
            root: config.to_path_buf(),
            backend: Backend::Postgres(pool),
        })
    }

    /// The instance transcode defaults the Transcode page edits.
    pub async fn transcode_defaults(&self) -> Result<TranscodeConfig, StoreError> {
        match &self.backend {
            Backend::Files => file::transcode_defaults(&self.root),
            Backend::Postgres(pool) => pg::transcode(pool).await,
        }
    }

    pub async fn save_transcode_defaults(&self, config: &TranscodeConfig) -> Result<(), StoreError> {
        match &self.backend {
            Backend::Files => file::save_transcode_defaults(&self.root, config),
            Backend::Postgres(pool) => pg::save_transcode(pool, config).await,
        }
    }

    /// Replace one channel's transcode overrides. The caller validates the
    /// patch first; this only stores it.
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

    /// The AI providers the AI page edits.
    pub async fn ai_config(&self) -> Result<AiConfig, StoreError> {
        match &self.backend {
            Backend::Files => file::ai_config(&self.root),
            Backend::Postgres(pool) => pg::ai(pool).await,
        }
    }

    /// Whether a saved key is (and where it lives) depends on the backend: the
    /// file backend writes `ai.json` with mode `0600`; Postgres stores the
    /// provider documents inside the database, where only its own access rules
    /// apply.
    pub async fn save_ai_config(&self, config: &AiConfig) -> Result<(), StoreError> {
        match &self.backend {
            Backend::Files => file::save_ai_config(&self.root, config),
            Backend::Postgres(pool) => pg::save_ai(pool, config).await,
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
}

/// The file backend. Writing goes through a temp file plus rename, so a
/// crash halfway never leaves a half-written document behind.
mod file {
    use super::*;

    pub fn transcode_defaults(root: &Path) -> Result<TranscodeConfig, StoreError> {
        let path = root.join(TRANSCODE_FILE);
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(TranscodeConfig::default());
            }
            Err(error) => return Err(error.into()),
        };
        let value: serde_json::Value = serde_json::from_str(&text)?;
        TranscodeConfig::parse(&value).map_err(StoreError::Transcode)
    }

    pub fn save_transcode_defaults(
        root: &Path,
        config: &TranscodeConfig,
    ) -> Result<(), StoreError> {
        let path = root.join(TRANSCODE_FILE);
        let tmp = path.with_extension("json.tmp");
        let json = serde_json::to_string_pretty(config)?;
        fs::write(&tmp, json)?;
        fs::rename(&tmp, &path)?;
        Ok(())
    }

    pub fn ai_config(root: &Path) -> Result<AiConfig, StoreError> {
        let path = root.join(AI_FILE);
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(AiConfig::default());
            }
            Err(error) => return Err(error.into()),
        };
        let value: serde_json::Value = serde_json::from_str(&text)?;
        // The single-endpoint document written before providers existed is
        // upgraded here and written back right away: the migration assigns the
        // provider its id, and rewriting on read is what keeps that id stable.
        let upgrade = value.get("providers").is_none();
        let config = AiConfig::parse(&value).map_err(StoreError::Ai)?;
        if upgrade {
            save_ai_config(root, &config)?;
        }
        Ok(config)
    }

    pub fn save_ai_config(root: &Path, config: &AiConfig) -> Result<(), StoreError> {
        let path = root.join(AI_FILE);
        let json = serde_json::to_string_pretty(config)?;
        write_private(&path, &json)?;
        Ok(())
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
        let path = root.join("channels").join(format!("{id}.json"));
        let text = fs::read_to_string(&path).map_err(|_| StoreError::NotFound(id))?;
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
        fs::remove_file(root.join("channels").join(format!("{id}.json")))
            .map_err(|_| StoreError::NotFound(id))
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
        let path = root.join("channels").join(format!("{}.json", channel.id));
        let tmp = path.with_extension("json.tmp");
        let json = serde_json::to_string_pretty(channel)?;
        fs::write(&tmp, json)?;
        fs::rename(&tmp, &path)?;
        Ok(())
    }
}

/// The Postgres backend. The three tables are invented here and owned by
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
            "CREATE TABLE IF NOT EXISTS transcode_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                config JSONB NOT NULL
            )",
            "CREATE TABLE IF NOT EXISTS ai_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                config JSONB NOT NULL
            )",
        ] {
            sqlx::query(statement).execute(pool).await?;
        }
        Ok(())
    }

    /// First run against an empty database: import the on-disk defaults, or
    /// fall back to the same defaults a fresh install would seed.
    pub async fn ensure_transcode(pool: &PgPool, config: &Path) -> Result<(), StoreError> {
        let present = sqlx::query("SELECT 1 FROM transcode_settings WHERE id = 1")
            .fetch_optional(pool)
            .await?
            .is_some();
        if present {
            return Ok(());
        }
        let text = match fs::read_to_string(config.join(TRANSCODE_FILE)) {
            Ok(text) => text,
            Err(_) => {
                save_transcode(pool, &TranscodeConfig::default()).await?;
                return Ok(());
            }
        };
        let value: serde_json::Value = serde_json::from_str(&text)?;
        let transcode = TranscodeConfig::parse(&value).map_err(StoreError::Transcode)?;
        save_transcode(pool, &transcode).await?;
        tracing::info!(path = %config.join(TRANSCODE_FILE).display(), "migrated transcode defaults into Postgres");
        Ok(())
    }

    pub async fn ensure_ai(pool: &PgPool, config: &Path) -> Result<(), StoreError> {
        let present = sqlx::query("SELECT 1 FROM ai_settings WHERE id = 1")
            .fetch_optional(pool)
            .await?
            .is_some();
        if present {
            return Ok(());
        }
        let text = match fs::read_to_string(config.join(AI_FILE)) {
            Ok(text) => text,
            Err(_) => {
                save_ai(pool, &AiConfig::default()).await?;
                return Ok(());
            }
        };
        let value: serde_json::Value = serde_json::from_str(&text)?;
        // `AiConfig::parse` upgrades the pre-providers shape, so the database
        // only ever holds the canonical provider list.
        let ai = AiConfig::parse(&value).map_err(StoreError::Ai)?;
        save_ai(pool, &ai).await?;
        tracing::info!(path = %config.join(AI_FILE).display(), "migrated AI providers into Postgres");
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

    pub async fn transcode(pool: &PgPool) -> Result<TranscodeConfig, StoreError> {
        match sqlx::query("SELECT config FROM transcode_settings WHERE id = 1")
            .fetch_optional(pool)
            .await?
        {
            Some(row) => {
                let value = row.try_get::<Json<serde_json::Value>, _>("config")?.0;
                TranscodeConfig::parse(&value).map_err(StoreError::Transcode)
            }
            None => Ok(TranscodeConfig::default()),
        }
    }

    pub async fn save_transcode(pool: &PgPool, config: &TranscodeConfig) -> Result<(), StoreError> {
        let value = serde_json::to_value(config)?;
        sqlx::query(
            "INSERT INTO transcode_settings (id, config) VALUES (1, $1)
             ON CONFLICT (id) DO UPDATE SET config = EXCLUDED.config",
        )
        .bind(Json(value))
        .execute(pool)
        .await?;
        Ok(())
    }

    pub async fn ai(pool: &PgPool) -> Result<AiConfig, StoreError> {
        match sqlx::query("SELECT config FROM ai_settings WHERE id = 1")
            .fetch_optional(pool)
            .await?
        {
            Some(row) => {
                let value = row.try_get::<Json<serde_json::Value>, _>("config")?.0;
                AiConfig::parse(&value).map_err(StoreError::Ai)
            }
            None => Ok(AiConfig::default()),
        }
    }

    pub async fn save_ai(pool: &PgPool, config: &AiConfig) -> Result<(), StoreError> {
        let value = serde_json::to_value(config)?;
        sqlx::query(
            "INSERT INTO ai_settings (id, config) VALUES (1, $1)
             ON CONFLICT (id) DO UPDATE SET config = EXCLUDED.config",
        )
        .bind(Json(value))
        .execute(pool)
        .await?;
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

/// Write `transcode.json` on first run, so the instance defaults are a real,
/// hand-editable file from the start rather than an implied everything-unset.
fn seed_transcode_file(config: &Path) -> Result<(), StoreError> {
    let path = config.join(TRANSCODE_FILE);
    if path.exists() {
        return Ok(());
    }
    file::save_transcode_defaults(config, &TranscodeConfig::default())?;
    tracing::info!(path = %path.display(), "wrote default transcode settings");
    Ok(())
}

/// Write `ai.json` on first run for the same reason as `transcode.json`: the
/// page starts from a real file naming the OpenAI defaults.
fn seed_ai_file(config: &Path) -> Result<(), StoreError> {
    let path = config.join(AI_FILE);
    if path.exists() {
        return Ok(());
    }
    file::save_ai_config(config, &AiConfig::default())?;
    tracing::info!(path = %path.display(), "wrote default AI settings");
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::NewChannel;
    use serde_json::json;

    /// The Postgres backend end to end. Skipped unless `TEST_DATABASE_URL` is
    /// set, so `cargo test` needs no database by default; point it at a
    /// scratch database when you do want the SQL layer exercised.
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

        // Channels: create, list, update, unique number, delete, missing.
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

        // Settings: the singleton rows round-trip their JSON documents.
        let mut ai = AiConfig::default();
        ai.create(&json!({
            "name": "DB", "priority": 1, "base_url": "https://example.test/v1",
            "api_key": "sk-secret", "chat_model": "gpt-4o-mini", "tts_model": "tts-1", "voice": "nova"
        }))
        .expect("provider");
        store.save_ai_config(&ai).await.expect("save ai");
        let reloaded = store.ai_config().await.expect("reload ai");
        assert_eq!(reloaded.providers.len(), 1);
        assert_eq!(reloaded.providers[0].name, "DB");
        assert_eq!(reloaded.providers[0].api_key, "sk-secret");

        let _ = std::fs::remove_dir_all(&dir);
    }
}

/// Write a file that holds a secret. The temporary file is created with mode
/// `0600` rather than written and then chmodded, so the key is never on disk
/// world-readable for even the instant between the two.
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