//! Background tasks. The Jellyfin library scan syncs the toggled-on libraries
//! on a daily timer or a cron override, and the Local Files and Past Tense
//! News scans walk their file folders daily. The core drives the media-source
//! sync directly through the SDK, so the tasks run without a browser; results
//! are kept for the Tasks page.

use std::path::Path;
use std::str::FromStr;
use std::sync::{Arc, Mutex, OnceLock};

use chrono::{DateTime, Duration, Utc};
use serde::{Deserialize, Serialize};

use channelflow_plugin_api::media::{Connection, SyncCtx};
use channelflow_plugin_api::tasks::{TaskHost, TaskInfo};

use crate::media::MediaSources;
use crate::store::{Store, StoreError};

const CONFIG_KEY: &str = "jellyfin_sync";
const RUNS_KEY: &str = "jellyfin_sync_runs";
const NEWS_CONFIG_KEY: &str = "news_scan";
const NEWS_RUNS_KEY: &str = "news_scan_runs";
const LOCAL_CONFIG_KEY: &str = "local_scan";
const LOCAL_RUNS_KEY: &str = "local_scan_runs";
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
    runs_for(store, RUNS_KEY).await
}

async fn runs_for(store: &Store, key: &str) -> Vec<SyncRun> {
    match store.task_get(key).await {
        Ok(Some(value)) => serde_json::from_value(value).unwrap_or_default(),
        _ => Vec::new(),
    }
}

async fn push_run_for(store: &Store, key: &str, run: SyncRun) -> Result<(), StoreError> {
    let mut list = runs_for(store, key).await;
    list.insert(0, run);
    list.truncate(MAX_RUNS);
    store.task_set(key, &serde_json::to_value(list)?).await
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

/// A simple daily-at-`HH:MM` scan task config, shared by the Local Files and
/// Past Tense News scan tasks.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DailyScanConfig {
    #[serde(default = "default_enabled")]
    pub enabled: bool,
    #[serde(default = "default_time")]
    pub daily_time: String,
}

impl Default for DailyScanConfig {
    fn default() -> Self {
        Self {
            enabled: default_enabled(),
            daily_time: default_time(),
        }
    }
}

/// The Past Tense News scan task's config.
pub type NewsScanConfig = DailyScanConfig;
/// The Local Files scan task's config.
pub type LocalScanConfig = DailyScanConfig;

/// `HH:MM` (server time) as a daily cron expression `M H * * *`.
pub fn daily_cron_expression(daily_time: &str) -> Option<String> {
    let (hour, minute) = daily_time.split_once(':')?;
    let hour: u32 = hour.trim().parse().ok()?;
    let minute: u32 = minute.trim().parse().ok()?;
    if hour > 23 || minute > 59 {
        return None;
    }
    Some(format!("{minute} {hour} * * *"))
}

async fn load_config(store: &Store, key: &str) -> DailyScanConfig {
    match store.task_get(key).await {
        Ok(Some(value)) => serde_json::from_value(value).unwrap_or_default(),
        _ => DailyScanConfig::default(),
    }
}

async fn save_config(store: &Store, key: &str, config: &DailyScanConfig) -> Result<(), StoreError> {
    store.task_set(key, &serde_json::to_value(config)?).await
}

/// Whether a daily scan task is due: the next daily occurrence after the last
/// run (or the past day, if there has never been one) has arrived.
async fn is_due_config(
    store: &Store,
    runs_key: &str,
    config: &DailyScanConfig,
    now: DateTime<Utc>,
) -> bool {
    if !config.enabled {
        return false;
    }
    let Some(expression) = daily_cron_expression(&config.daily_time) else {
        return false;
    };
    let Some(schedule) = parse(&expression) else {
        return false;
    };
    let last = runs_for(store, runs_key)
        .await
        .first()
        .map(|run| run.at)
        .unwrap_or_else(|| now - Duration::days(1));
    schedule.after(&last).next().is_some_and(|next| next <= now)
}

/// The Past Tense News scan task: scans the news connections' event folders
/// into the media database. Scheduled like the library scan, but only touches
/// `news` connections (never the slow media-server walks).
pub async fn news_load(store: &Store) -> DailyScanConfig {
    load_config(store, NEWS_CONFIG_KEY).await
}

pub async fn news_save(store: &Store, config: &DailyScanConfig) -> Result<(), StoreError> {
    save_config(store, NEWS_CONFIG_KEY, config).await
}

pub async fn news_runs(store: &Store) -> Vec<SyncRun> {
    runs_for(store, NEWS_RUNS_KEY).await
}

pub async fn news_is_due(store: &Store, config: &DailyScanConfig, now: DateTime<Utc>) -> bool {
    is_due_config(store, NEWS_RUNS_KEY, config, now).await
}

pub fn news_cron_expression(daily_time: &str) -> Option<String> {
    daily_cron_expression(daily_time)
}

/// Scan every Past Tense News connection's event folders into the media DB,
/// or just one when `only_connection` is given (the Library page's Sync).
pub async fn run_news_scan(
    store: &Store,
    media: &MediaSources,
    config_dir: &Path,
    trigger: &str,
    only_connection: Option<i64>,
) -> Result<SyncRun, StoreError> {
    run_scanner(
        store,
        media,
        config_dir,
        trigger,
        only_connection,
        Some("news"),
        NEWS_RUNS_KEY,
        "news-scan",
        "Past Tense News scan",
        None,
    )
    .await
}

/// The Local Files scan task: scans each local connection's configured media
/// folders into the database (one item per media file, with its .nfo metadata
/// and poster). Like the news scan it only touches `local` connections.
pub async fn local_load(store: &Store) -> DailyScanConfig {
    load_config(store, LOCAL_CONFIG_KEY).await
}

pub async fn local_save(store: &Store, config: &DailyScanConfig) -> Result<(), StoreError> {
    save_config(store, LOCAL_CONFIG_KEY, config).await
}

pub async fn local_runs(store: &Store) -> Vec<SyncRun> {
    runs_for(store, LOCAL_RUNS_KEY).await
}

pub async fn local_is_due(store: &Store, config: &DailyScanConfig, now: DateTime<Utc>) -> bool {
    is_due_config(store, LOCAL_RUNS_KEY, config, now).await
}

pub fn local_cron_expression(daily_time: &str) -> Option<String> {
    daily_cron_expression(daily_time)
}

/// Scan every Local Files connection's folders into the media DB, or just one
/// when `only_connection` is given (the Library page's Sync).
pub async fn run_local_scan(
    store: &Store,
    media: &MediaSources,
    config_dir: &Path,
    trigger: &str,
    only_connection: Option<i64>,
) -> Result<SyncRun, StoreError> {
    run_scanner(
        store,
        media,
        config_dir,
        trigger,
        only_connection,
        Some("local"),
        LOCAL_RUNS_KEY,
        "local-scan",
        "Local Files scan",
        None,
    )
    .await
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
    run_scanner(
        store,
        media,
        config_dir,
        trigger,
        only_connection,
        None,
        RUNS_KEY,
        "jellyfin-sync",
        "Jellyfin library scan",
        Some(format!("/api/plugins/{JELLYFIN_PLUGIN}/progress")),
    )
    .await
}

/// Shared scan driver: runs each matching connection's source sync and records
/// one run under `runs_key`, with the running-task slot filled while it goes.
async fn run_scanner(
    store: &Store,
    media: &MediaSources,
    config_dir: &Path,
    trigger: &str,
    only_connection: Option<i64>,
    only_kind: Option<&str>,
    runs_key: &str,
    running_kind: &str,
    running_title: &str,
    progress_url: Option<String>,
) -> Result<SyncRun, StoreError> {
    // Only one scan at a time: a trigger that lands while a scan is running is
    // refused, not stacked (see `scan_slot`). The guard is held for the whole
    // scan and dropped however it ends.
    let _scan = match scan_slot().try_lock() {
        Ok(guard) => guard,
        Err(_) => {
            tracing::info!(
                task = running_kind,
                "a scan is already running; ignoring this trigger"
            );
            return Err(StoreError::Plugin(format!(
                "{running_title} is already running"
            )));
        }
    };
    // A sync can outlive the tab that started it, so mark it running in a
    // process-wide registry the web UI can read on (re)load; clear it however
    // the sync ends. The registry is in-memory, so a server restart leaves
    // nothing stale behind.
    set_running(RunningTask {
        key: running_kind.to_string(),
        kind: running_kind.to_string(),
        title: running_title.to_string(),
        started_at: Utc::now(),
        progress_url,
        label: None,
        current: None,
        total: None,
    });
    let outcome = run_sync_inner(store, media, config_dir, trigger, only_connection, only_kind).await;
    clear_running(running_kind);
    if let Ok(run) = &outcome {
        let _ = push_run_for(store, runs_key, run.clone()).await;
    }
    outcome
}

/// One running background task, as the web UI's status popup needs it: a title
/// to show immediately, an optional live detail line (pushed by the plugin
/// that owns the work), and — for a source that exposes one — a JSON-returning
/// URL that yields `{ progress: { label, current, total } }`.
#[derive(Debug, Clone, Serialize)]
pub struct RunningTask {
    /// Stable id. The scans use their own kind (`jellyfin-sync`); a plugin task
    /// is `<plugin id>:<plugin's key>`.
    pub key: String,
    /// Who owns the work: a scan kind, or a plugin's manifest id.
    pub kind: String,
    pub title: String,
    pub started_at: DateTime<Utc>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub progress_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub label: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub total: Option<u64>,
}

fn running_slot() -> &'static Mutex<Vec<RunningTask>> {
    static SLOT: OnceLock<Mutex<Vec<RunningTask>>> = OnceLock::new();
    SLOT.get_or_init(|| Mutex::new(Vec::new()))
}

/// Every task running right now, oldest first. Read by `GET /api/tasks/running`
/// so a reload (or a fresh browser) can bring the popups back.
pub fn running() -> Vec<RunningTask> {
    running_slot().lock().unwrap().clone()
}

/// Start a task, or replace the one already running under the same key.
fn set_running(task: RunningTask) {
    let mut slot = running_slot().lock().unwrap();
    match slot.iter_mut().find(|running| running.key == task.key) {
        Some(existing) => *existing = task,
        None => slot.push(task),
    }
}

/// Update a running task's detail line, and its counts when given. A task that
/// is not (or no longer) running is ignored, so a late progress report from
/// work that already ended cannot resurrect it.
fn update_running(key: &str, label: Option<String>, current: Option<u64>, total: Option<u64>) {
    let mut slot = running_slot().lock().unwrap();
    let Some(task) = slot.iter_mut().find(|running| running.key == key) else {
        return;
    };
    if let Some(label) = label {
        task.label = Some(label);
    }
    if current.is_some() {
        task.current = current;
    }
    if total.is_some() {
        task.total = total;
    }
}

/// Drop a task: it ended, one way or another.
fn clear_running(key: &str) {
    running_slot().lock().unwrap().retain(|task| task.key != key);
}

/// Drop every task one plugin owns. Called when a plugin is disabled or
/// unloaded, so it cannot leave a phantom popup behind.
pub fn clear_plugin(plugin_id: &str) {
    running_slot()
        .lock()
        .unwrap()
        .retain(|task| task.kind != plugin_id);
}

/// The base's end of the plugin task hook: reports land in the same registry
/// the scans use, keyed `<plugin id>:<plugin key>`.
struct PluginTasks {
    plugin: String,
}

impl PluginTasks {
    fn key(&self, key: &str) -> String {
        format!("{}:{}", self.plugin, key)
    }
}

impl TaskHost for PluginTasks {
    fn task_started(&self, task: TaskInfo) {
        set_running(RunningTask {
            key: self.key(&task.key),
            kind: self.plugin.clone(),
            title: task.title,
            started_at: Utc::now(),
            progress_url: None,
            label: task.label,
            current: task.current,
            total: task.total,
        });
    }

    fn task_progress(&self, key: &str, label: &str, current: Option<u64>, total: Option<u64>) {
        update_running(&self.key(key), Some(label.to_string()), current, total);
    }

    fn task_finished(&self, key: &str) {
        clear_running(&self.key(key));
    }
}

/// A task host for one plugin, handed to it as `PluginApi::tasks`.
pub fn plugin_host(plugin_id: &str) -> Arc<dyn TaskHost> {
    Arc::new(PluginTasks {
        plugin: plugin_id.to_string(),
    })
}

/// The one scan that may run at a time. The Jellyfin, news and local scans all
/// write the same catalog tables behind a single unique `match_key` index, so
/// running two at once only parks each one on the other's row locks: a delete
/// that matches nothing yet takes seconds is a lock wait, not work, and on a
/// slow (e.g. containerised) database that stretches a scan from minutes into
/// hours. Worse, a run is only recorded once it finishes, so `is_due` still
/// reads "due" while it is in flight and the next trigger starts *another*
/// full scan on top of it. This slot makes triggers refuse instead of stack.
fn scan_slot() -> &'static tokio::sync::Mutex<()> {
    static SLOT: OnceLock<tokio::sync::Mutex<()>> = OnceLock::new();
    SLOT.get_or_init(|| tokio::sync::Mutex::new(()))
}

/// True while a scan is running, so the daily timers skip their tick rather
/// than queue another full sync behind this one.
pub fn scan_running() -> bool {
    scan_slot().try_lock().is_err()
}

async fn run_sync_inner(
    store: &Store,
    media: &MediaSources,
    config_dir: &Path,
    trigger: &str,
    only_connection: Option<i64>,
    only_kind: Option<&str>,
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
        if only_kind.is_some_and(|wanted| kind != wanted) {
            continue;
        }
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
    tracing::info!(
        added = run.added,
        updated = run.updated,
        errors = run.errors,
        "library scan finished"
    );
    Ok(run)
}
