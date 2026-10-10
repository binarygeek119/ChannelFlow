//! Background tasks. Today there is one: the Jellyfin library scan, which
//! syncs the toggled-on libraries on a daily timer or a cron override. The
//! core drives the media-source sync directly through the SDK, so the task
//! runs without a browser; results are kept for the Tasks page.

use std::path::Path;
use std::str::FromStr;

use chrono::{DateTime, Duration, Utc};
use serde::{Deserialize, Serialize};

use channelflow_plugin_api::media::{Connection, SyncCtx};

use crate::media::MediaSources;
use crate::store::{Store, StoreError};

const CONFIG_KEY: &str = "jellyfin_sync";
const RUNS_KEY: &str = "jellyfin_sync_runs";
const MAX_RUNS: usize = 20;
/// Jellyfin is the only source with a built-in scan today; its plugin id owns
/// the connection's tables.
const JELLYFIN_PLUGIN: &str = "com.channelflow.jellyfin";

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncSchedule {
    /// "daily" or "cron".
    #[serde(default = "default_mode")]
    pub mode: String,
    /// Local server time "HH:MM", used when mode is "daily".
    #[serde(default = "default_time")]
    pub daily_time: String,
    /// A crontab expression, used when mode is "cron".
    #[serde(default)]
    pub cron: String,
}

impl Default for SyncSchedule {
    fn default() -> Self {
        Self {
            mode: default_mode(),
            daily_time: default_time(),
            cron: String::new(),
        }
    }
}

fn default_mode() -> String {
    "daily".to_string()
}

fn default_time() -> String {
    "03:00".to_string()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncConfig {
    #[serde(default = "default_enabled")]
    pub enabled: bool,
    #[serde(default)]
    pub schedule: SyncSchedule,
}

impl Default for SyncConfig {
    fn default() -> Self {
        Self {
            enabled: default_enabled(),
            schedule: SyncSchedule::default(),
        }
    }
}

fn default_enabled() -> bool {
    true
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncRun {
    pub at: DateTime<Utc>,
    /// "schedule", "manual", or "library".
    pub trigger: String,
    pub connections: usize,
    pub added: u64,
    pub updated: u64,
    pub removed: u64,
    pub errors: u64,
}

pub async fn load(store: &Store) -> SyncConfig {
    match store.task_get(CONFIG_KEY).await {
        Ok(Some(value)) => serde_json::from_value(value).unwrap_or_default(),
        _ => SyncConfig::default(),
    }
}

pub async fn save(store: &Store, config: &SyncConfig) -> Result<(), StoreError> {
    store
        .task_set(CONFIG_KEY, &serde_json::to_value(config)?)
        .await
}

pub async fn runs(store: &Store) -> Vec<SyncRun> {
    match store.task_get(RUNS_KEY).await {
        Ok(Some(value)) => serde_json::from_value(value).unwrap_or_default(),
        _ => Vec::new(),
    }
}

async fn push_run(store: &Store, run: SyncRun) -> Result<(), StoreError> {
    let mut list = runs(store).await;
    list.insert(0, run);
    list.truncate(MAX_RUNS);
    store.task_set(RUNS_KEY, &serde_json::to_value(list)?).await
}

/// The cron expression a schedule means. Daily `HH:MM` becomes `M H * * *`.
pub fn cron_expression(schedule: &SyncSchedule) -> Option<String> {
    if schedule.mode == "cron" {
        let trimmed = schedule.cron.trim();
        if trimmed.is_empty() {
            return None;
        }
        return Some(trimmed.to_string());
    }
    let (hour, minute) = schedule.daily_time.split_once(':')?;
    let hour: u32 = hour.trim().parse().ok()?;
    let minute: u32 = minute.trim().parse().ok()?;
    if hour > 23 || minute > 59 {
        return None;
    }
    Some(format!("{minute} {hour} * * *"))
}

fn parse(expression: &str) -> Option<cron::Schedule> {
    cron::Schedule::from_str(expression).ok()
}

/// Whether the schedule is due: the next occurrence after the last run (or
/// the past day, if there has never been one) has arrived.
pub async fn is_due(store: &Store, config: &SyncConfig, now: DateTime<Utc>) -> bool {
    if !config.enabled {
        return false;
    }
    let Some(expression) = cron_expression(&config.schedule) else {
        return false;
    };
    let Some(schedule) = parse(&expression) else {
        return false;
    };
    let last = runs(store)
        .await
        .first()
        .map(|run| run.at)
        .unwrap_or_else(|| now - Duration::days(1));
    schedule
        .after(&last)
        .next()
        .is_some_and(|next| next <= now)
}

/// Sync every connection (or one), each with the libraries toggled on for it.
/// A connection with no stored selection syncs all of its libraries.
pub async fn run_sync(
    store: &Store,
    media: &MediaSources,
    config_dir: &Path,
    trigger: &str,
    only_connection: Option<i64>,
) -> Result<SyncRun, StoreError> {
    let registry = store.plugin_registry().await?;
    let installed = |id: &str| registry.get(id).is_some();
    let image_root = config_dir.join("Images");
    let mut run = SyncRun {
        at: Utc::now(),
        trigger: trigger.to_string(),
        connections: 0,
        added: 0,
        updated: 0,
        removed: 0,
        errors: 0,
    };
    for row in store.connection_list().await? {
        let id = row["id"].as_i64().unwrap_or_default();
        if only_connection.is_some_and(|want| want != id) {
            continue;
        }
        let kind = row["kind"].as_str().unwrap_or_default().to_string();
        let Some(source) = media.find_installed(&kind, &installed) else {
            continue;
        };
        let config = row["config"].clone();
        let connection: Connection = match serde_json::from_value(config.clone()) {
            Ok(connection) => connection,
            Err(error) => {
                tracing::warn!(%error, connection = id, "skipping a connection with an invalid config");
                continue;
            }
        };
        let all = source.list_libraries(&connection, &connection.api_key).await;
        let selected = config.get("enabled_libraries").and_then(|v| v.as_array());
        let libraries = match selected {
            Some(ids) => {
                let wanted: Vec<String> = ids
                    .iter()
                    .filter_map(|id| id.as_str().map(str::to_string))
                    .collect();
                all.into_iter()
                    .filter(|library| wanted.contains(&library.remote_id))
                    .collect()
            }
            None => all,
        };
        let ctx = SyncCtx {
            connection: connection.clone(),
            api_key: connection.api_key.clone(),
            enabled_libraries: libraries,
            connection_id: id,
            db: store.plugin_database(JELLYFIN_PLUGIN).await,
            image_root: image_root.clone(),
            remaps: connection.path_remaps.clone(),
            catalog: Some(std::sync::Arc::new(store.clone())),
        };
        tracing::info!(connection = id, kind, "library scan running");
        let report = source.sync_library(ctx).await;
        run.connections += 1;
        run.added += report.added;
        run.updated += report.updated;
        run.removed += report.removed;
        run.errors += report.errors;
    }
    push_run(store, run.clone()).await?;
    tracing::info!(
        added = run.added,
        updated = run.updated,
        errors = run.errors,
        "library scan finished"
    );
    Ok(run)
}
