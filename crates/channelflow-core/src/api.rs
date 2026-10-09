//! HTTP surface: the JSON API for channels and plugins plus the static web UI
//! shell. Plugins contribute their own routers, nested under `/api/plugins`.

use std::sync::Arc;

use axum::{
    extract::{Path, Query, State},
    http::StatusCode,
    response::{IntoResponse, Response},
    routing::{get, put},
    Json, Router,
};
use serde::Deserialize;
use serde_json::json;
use tokio::sync::Mutex;
use uuid::Uuid;

use crate::model::{Channel, NewChannel, UpdateChannel};
use crate::plugin::{PluginManager, StoreClient};
use crate::store::{Store, StoreError};

/// Facts about the running process that only `main` can know: where the config
/// directory is, what port is bound, and when the process started.
#[derive(Clone)]
pub struct AboutInfo {
    pub config_folder: String,
    pub listen_port: u16,
    pub started: std::time::Instant,
}

/// What handlers may reach: the channel store, the process facts above, and
/// the loaded plugins. The outbound HTTP client lives in each plugin's own
/// state now that features are plugins.
#[derive(Clone)]
pub struct AppState {
    pub store: Store,
    pub about: AboutInfo,
    pub plugins: Arc<Mutex<PluginManager>>,
    pub store_client: Arc<StoreClient>,
}

pub fn router(
    store: Store,
    about_info: AboutInfo,
    plugins: Arc<Mutex<PluginManager>>,
    plugin_routers: Vec<(String, Router)>,
    store_client: Arc<StoreClient>,
) -> Router {
    let mut app = Router::new()
        .route("/", get(index))
        .route("/app.css", get(css))
        .route("/app.js", get(js))
        .route("/logo.png", get(logo))
        .route("/favicon.ico", get(favicon))
        .route("/favicon-32x32.png", get(favicon_32))
        .route("/favicon-16x16.png", get(favicon_16))
        .route("/apple-touch-icon.png", get(apple_touch_icon))
        .route("/api/about", get(about))
        .route("/api/health", get(health))
        .route("/api/channels", get(list_channels).post(create_channel))
        .route(
            "/api/channels/{id}",
            get(get_channel).put(update_channel).delete(delete_channel),
        )
        .route("/api/plugins", get(list_plugins))
        .route("/api/plugins/store", get(store_plugins))
        .route(
            "/api/plugins/{id}/install",
            put(install_plugin).delete(remove_plugin),
        )
        .route("/api/plugins/{id}/update", put(update_plugin))
        .route("/api/plugins/{id}/enable", put(enable_plugin))
        .route("/api/plugins/{id}/disable", put(disable_plugin))
        .route("/live/{asset}", get(live_pending))
        .with_state(AppState {
            store,
            about: about_info,
            plugins,
            store_client,
        });
    for (id, plugin_router) in plugin_routers {
        app = app.nest(&format!("/api/plugins/{id}"), plugin_router);
    }
    app
}

/// Storage errors translated into HTTP status codes.
struct ApiError(StoreError);

impl From<StoreError> for ApiError {
    fn from(error: StoreError) -> Self {
        ApiError(error)
    }
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        let (status, message) = match &self.0 {
            StoreError::NotFound(_) => (StatusCode::NOT_FOUND, self.0.to_string()),
            StoreError::DuplicateNumber(_) => (StatusCode::CONFLICT, self.0.to_string()),
            StoreError::Invalid(_) => (StatusCode::BAD_REQUEST, self.0.to_string()),
            StoreError::Plugin(_) => (StatusCode::BAD_REQUEST, self.0.to_string()),
            StoreError::PluginNotFound(_) => (StatusCode::NOT_FOUND, self.0.to_string()),
            StoreError::Upstream(_) => (StatusCode::BAD_GATEWAY, self.0.to_string()),
            StoreError::Io(_) | StoreError::Json(_) | StoreError::Database(_) => {
                tracing::error!(error = %self.0, "storage failure");
                (
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "internal storage error".to_string(),
                )
            }
        };
        (status, Json(json!({ "error": message }))).into_response()
    }
}

async fn health() -> Json<serde_json::Value> {
    Json(json!({
        "status": "ok",
        "name": "ChannelFlow",
        "version": env!("CARGO_PKG_VERSION"),
    }))
}

/// Everything the About page reports, in the shape 1.0.0's page expects.
///
/// `app` and `system` are filled in here; `stream` is left empty because there
/// is no encoder until the playout milestone, and the page says so rather than
/// reporting numbers that do not exist yet. Rows the platform cannot answer
/// (no `/proc`, no time zone file) serialise to `null` and the client drops
/// them instead of rendering a blank cell.
async fn about(State(state): State<AppState>) -> Json<serde_json::Value> {
    let process = &state.about;
    Json(json!({
        "app": {
            "author": "binarygeek119",
            "authorUrl": "https://github.com/binarygeek119",
            "version": env!("CARGO_PKG_VERSION"),
            "revision": option_env!("GIT_SHA").unwrap_or_default(),
            "runtime": option_env!("RUSTC_VERSION").unwrap_or_default(),
            "packagingLabel": if std::path::Path::new("/.dockerenv").exists() {
                "Docker"
            } else {
                "Non-Docker"
            },
            "homepage": "https://github.com/binarygeek119/ChannelFlow",
        },
        "system": {
            "os": std::env::consts::OS,
            "architecture": std::env::consts::ARCH,
            "machineName": hostname(),
            "processorCount": std::thread::available_parallelism().ok().map(|n| n.get()),
            "workingSet": memory_working_set(),
            "uptime": format_uptime(process.started.elapsed()),
            "timeZone": time_zone(),
            "listenPort": process.listen_port,
            "configFolder": process.config_folder,
        },
        "stream": {},
    }))
}

fn hostname() -> Option<String> {
    read_trimmed("/proc/sys/kernel/hostname").or_else(|| read_trimmed("/etc/hostname"))
}

/// Resident set size from `/proc/self/status`. Linux only, so the row simply
/// does not appear elsewhere.
fn memory_working_set() -> Option<String> {
    let status = read_trimmed("/proc/self/status")?;
    let line = status.lines().find(|line| line.starts_with("VmRSS:"))?;
    let kibibytes: f64 = line.split_whitespace().nth(1)?.parse().ok()?;
    Some(format_bytes(kibibytes * 1024.0))
}

/// Debian and Ubuntu carry the zone name in `/etc/timezone`; other distros
/// only have the `/etc/localtime` symlink. Fall back to the numeric offset so
/// the row still shows something true.
fn time_zone() -> Option<String> {
    if let Some(zone) = read_trimmed("/etc/timezone") {
        return Some(zone);
    }
    if let Ok(link) = std::fs::read_link("/etc/localtime") {
        let target = link.to_string_lossy();
        if let Some(offset) = target.find("zoneinfo/") {
            return Some(target[offset + "zoneinfo/".len()..].to_string());
        }
    }
    Some(chrono::Local::now().offset().to_string())
}

fn read_trimmed(path: &str) -> Option<String> {
    let text = std::fs::read_to_string(path).ok()?;
    let trimmed = text.trim().to_string();
    (!trimmed.is_empty()).then_some(trimmed)
}

fn format_uptime(elapsed: std::time::Duration) -> String {
    let total = elapsed.as_secs();
    let (days, hours, minutes) = (total / 86_400, (total % 86_400) / 3_600, (total % 3_600) / 60);
    let mut parts = Vec::new();
    if days > 0 {
        parts.push(format!("{days}d"));
    }
    if hours > 0 {
        parts.push(format!("{hours}h"));
    }
    if minutes > 0 {
        parts.push(format!("{minutes}m"));
    }
    if parts.is_empty() {
        parts.push(format!("{}s", total));
    }
    parts.join(" ")
}

fn format_bytes(bytes: f64) -> String {
    const KIB: f64 = 1024.0;
    const MIB: f64 = KIB * 1024.0;
    const GIB: f64 = MIB * 1024.0;
    if bytes >= GIB {
        format!("{:.1} GiB", bytes / GIB)
    } else if bytes >= MIB {
        format!("{:.1} MiB", bytes / MIB)
    } else {
        format!("{:.1} KiB", bytes / KIB)
    }
}

async fn list_channels(State(state): State<AppState>) -> Result<Json<Vec<Channel>>, ApiError> {
    Ok(Json(state.store.list().await?))
}

async fn get_channel(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
) -> Result<Json<Channel>, ApiError> {
    Ok(Json(state.store.get(id).await?))
}

async fn create_channel(
    State(state): State<AppState>,
    Json(input): Json<NewChannel>,
) -> Result<(StatusCode, Json<Channel>), ApiError> {
    let channel = state.store.create(input).await?;
    Ok((StatusCode::CREATED, Json(channel)))
}

async fn update_channel(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
    Json(input): Json<UpdateChannel>,
) -> Result<Json<Channel>, ApiError> {
    Ok(Json(state.store.update(id, input).await?))
}

async fn delete_channel(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, ApiError> {
    state.store.delete(id).await?;
    Ok(StatusCode::NO_CONTENT)
}

/// The Installed tab: the plugins in the registry, each joined with what the
/// manager knows about it (name, category, permissions, health).
async fn list_plugins(
    State(state): State<AppState>,
) -> Result<Json<serde_json::Value>, ApiError> {
    Ok(Json(installed_view(&state).await?))
}

/// The Store tab: the repository's catalog, each plugin marked installed or
/// not, bundled-in-this-build or not, with update status.
async fn store_plugins(
    State(state): State<AppState>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let remote = state
        .store_client
        .fetch()
        .await
        .map_err(StoreError::Upstream)?;
    let registry = state.store.plugin_registry().await?;
    let base = env!("CARGO_PKG_VERSION");
    let bundled = {
        let manager = state.plugins.lock().await;
        manager.catalog()
    };

    let plugins: Vec<serde_json::Value> = remote
        .iter()
        .map(|plugin| {
            let latest = plugin.latest();
            let latest_version = latest.map(|v| v.version.clone()).unwrap_or_default();
            let compatible = latest
                .map(|v| {
                    channelflow_plugin_api::compatible(
                        base,
                        &v.min_base_version,
                        &v.max_base_version,
                    )
                })
                .unwrap_or(false);
            let installed = registry.get(&plugin.id);
            let update_available = installed
                .map(|entry| version_is_newer(&latest_version, &entry.version))
                .unwrap_or(false);
            json!({
                "id": plugin.id,
                "name": plugin.name,
                "description": plugin.description,
                "owner": plugin.owner,
                "category": plugin.category,
                "homepage": plugin.homepage,
                "image_url": plugin.image_url,
                "latest_version": latest_version,
                "compatible": compatible,
                "installed": installed.is_some(),
                "bundled": bundled.iter().any(|entry| entry["id"] == json!(plugin.id)),
                "installed_version": installed.map(|entry| entry.version.clone()),
                "update_available": update_available,
            })
        })
        .collect();

    Ok(Json(json!({
        "source": state.store_client.url(),
        "plugins": plugins,
    })))
}

async fn install_plugin(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Json<serde_json::Value>, ApiError> {
    // Only plugins compiled into this build can be installed today; the store
    // still shows the rest. Dynamic loading is the milestone that lifts this.
    let version = {
        let manager = state.plugins.lock().await;
        manager
            .catalog()
            .into_iter()
            .find(|entry| entry["id"] == json!(id))
            .and_then(|entry| entry["version"].as_str().map(str::to_string))
    }
    .ok_or_else(|| {
        StoreError::Plugin(format!(
            "{id} is not part of this build — dynamic plugin loading is not enabled yet"
        ))
    })?;

    let mut registry = state.store.plugin_registry().await?;
    registry.install(&id, &version);
    state.store.save_plugin_registry(&registry).await?;
    {
        let mut manager = state.plugins.lock().await;
        manager
            .enable(&id)
            .await
            .map_err(|error| StoreError::Plugin(error.to_string()))?;
    }
    Ok(Json(installed_view(&state).await?))
}

async fn update_plugin(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let remote = state
        .store_client
        .fetch()
        .await
        .map_err(StoreError::Upstream)?;
    let latest = remote
        .iter()
        .find(|plugin| plugin.id == id)
        .and_then(|plugin| plugin.latest())
        .map(|version| version.version.clone());
    let registry = state.store.plugin_registry().await?;
    let installed = registry
        .get(&id)
        .ok_or_else(|| StoreError::PluginNotFound(id.clone()))?;
    match latest {
        Some(latest) if version_is_newer(&latest, &installed.version) => {
            Err(StoreError::Plugin(format!(
                "plugins are compiled into ChannelFlow, so {id} cannot update itself from {}. Update ChannelFlow to get {latest}.",
                installed.version
            ))
            .into())
        }
        _ => Ok(Json(json!({ "message": "already up to date" }))),
    }
}

#[derive(Deserialize)]
struct RemoveQuery {
    #[serde(default)]
    drop_database: bool,
}

async fn remove_plugin(
    State(state): State<AppState>,
    Path(id): Path<String>,
    Query(query): Query<RemoveQuery>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let mut registry = state.store.plugin_registry().await?;
    if !registry.remove(&id) {
        return Err(StoreError::PluginNotFound(id).into());
    }
    state.store.save_plugin_registry(&registry).await?;
    {
        let mut manager = state.plugins.lock().await;
        let _ = manager.disable(&id).await;
    }
    // Dropping is the destructive choice: it erases the plugin's tables and
    // key/value storage. Keeping leaves them for a reinstall.
    let dropped = if query.drop_database {
        state.store.drop_plugin_data(&id).await?
    } else {
        0
    };
    Ok(Json(json!({
        "removed": id,
        "dropped": dropped,
        "kept": !query.drop_database,
        "plugins": installed_view(&state).await?["plugins"],
    })))
}

async fn enable_plugin(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Json<serde_json::Value>, ApiError> {
    {
        let mut manager = state.plugins.lock().await;
        manager
            .enable(&id)
            .await
            .map_err(|error| StoreError::Plugin(error.to_string()))?;
    }
    let mut registry = state.store.plugin_registry().await?;
    if registry.set_enabled(&id, true) {
        state.store.save_plugin_registry(&registry).await?;
    }
    Ok(Json(installed_view(&state).await?))
}

async fn disable_plugin(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Json<serde_json::Value>, ApiError> {
    {
        let mut manager = state.plugins.lock().await;
        manager
            .disable(&id)
            .await
            .map_err(|error| StoreError::Plugin(error.to_string()))?;
    }
    let mut registry = state.store.plugin_registry().await?;
    if registry.set_enabled(&id, false) {
        state.store.save_plugin_registry(&registry).await?;
    }
    Ok(Json(installed_view(&state).await?))
}

/// The installed plugins, each joined with the manager's view of it.
async fn installed_view(state: &AppState) -> Result<serde_json::Value, ApiError> {
    let registry = state.store.plugin_registry().await?;
    let manager = state.plugins.lock().await;
    let catalog = manager.catalog();
    let plugins: Vec<serde_json::Value> = registry
        .installed
        .iter()
        .map(|installed| {
            let known = catalog.iter().find(|entry| entry["id"] == json!(installed.id));
            match known {
                Some(entry) => json!({
                    "id": installed.id,
                    "version": installed.version,
                    "enabled": installed.enabled,
                    "bundled": true,
                    "name": entry["name"],
                    "category": entry["category"],
                    "description": entry["description"],
                    "health": entry["health"],
                    "permissions": entry["permissions"],
                    "ui_contributions": entry["ui_contributions"],
                }),
                None => json!({
                    "id": installed.id,
                    "version": installed.version,
                    "enabled": installed.enabled,
                    "bundled": false,
                    "name": installed.id,
                    "category": "",
                    "description": "This plugin is not part of this build.",
                    "health": serde_json::Value::Null,
                    "permissions": [],
                    "ui_contributions": [],
                }),
            }
        })
        .collect();
    Ok(json!({ "plugins": plugins }))
}

/// True when `candidate` is a higher version than `current`.
fn version_is_newer(candidate: &str, current: &str) -> bool {
    match (
        semver::Version::parse(candidate.trim_start_matches('v')),
        semver::Version::parse(current.trim_start_matches('v')),
    ) {
        (Ok(candidate), Ok(current)) => candidate > current,
        _ => false,
    }
}

/// The streaming paths the Live TV page advertises.
///
/// Only port `8097` is published and the encoder listens inside the container
/// on another port, so every URL the page shows is written against ChannelFlow's
/// own origin. Until the playout milestone produces those streams these paths
/// answer `503` with a sentence rather than `404`: a player pointed at one gets
/// an honest "not yet" instead of "no such thing", and the milestone repoints
/// these at ErsatzTV next without the page changing.
async fn live_pending() -> Response {
    (
        StatusCode::SERVICE_UNAVAILABLE,
        [(
            axum::http::header::CONTENT_TYPE,
            "text/plain; charset=utf-8",
        )],
        "Live video arrives with the playout milestone — ChannelFlow is not encoding yet.\n",
    )
        .into_response()
}

// The UI is compiled into the binary so the shipped image needs no asset
// directory and cannot start with a half-copied web root.
async fn index() -> Response {
    static_response(
        "text/html; charset=utf-8",
        include_str!("../static/index.html"),
    )
}

async fn css() -> Response {
    static_response("text/css; charset=utf-8", include_str!("../static/app.css"))
}

async fn js() -> Response {
    static_response(
        "text/javascript; charset=utf-8",
        include_str!("../static/app.js"),
    )
}

/// Serve one compiled-in asset.
///
/// `no-cache` matters more than it looks: these bytes change with the binary
/// but carry no ETag or Last-Modified, so a browser that cached them
/// heuristically would otherwise pair a new `index.html` with a stale
/// `app.js` after an upgrade. The UI is a few dozen kilobytes, so always
/// refetching is cheaper than tracking a version suffix by hand.
fn static_response(content_type: &'static str, body: impl Into<axum::body::Bytes>) -> Response {
    (
        [
            (axum::http::header::CONTENT_TYPE, content_type),
            (axum::http::header::CACHE_CONTROL, "no-cache"),
        ],
        body.into(),
    )
        .into_response()
}

async fn logo() -> Response {
    static_response("image/png", include_bytes!("../static/logo.png").as_slice())
}

async fn favicon() -> Response {
    static_response(
        "image/x-icon",
        include_bytes!("../static/favicon.ico").as_slice(),
    )
}

async fn favicon_32() -> Response {
    static_response(
        "image/png",
        include_bytes!("../static/favicon-32x32.png").as_slice(),
    )
}

async fn favicon_16() -> Response {
    static_response(
        "image/png",
        include_bytes!("../static/favicon-16x16.png").as_slice(),
    )
}

async fn apple_touch_icon() -> Response {
    static_response(
        "image/png",
        include_bytes!("../static/apple-touch-icon.png").as_slice(),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use channelflow_plugin_api::plugin::{PluginApi, PluginLogger};

    /// The router is built once at startup, so a malformed path — a segment
    /// that mixes a parameter with a literal like `/live/{n}.m3u8`, say —
    /// panics there instead of failing a request. Building it here, with the
    /// AI plugin loaded and its routes nested, turns that class of mistake
    /// into a test failure rather than a server that will not boot.
    #[tokio::test]
    async fn the_router_builds() {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("channelflow-router-{stamp}"));
        let store = Store::open(&dir).expect("open store");
        let http = reqwest::Client::new();

        let mut manager = PluginManager::new(env!("CARGO_PKG_VERSION"));
        let plugin = channelflow_plugin_ai::plugin();
        let manifest = plugin.metadata().clone();
        let api = PluginApi {
            id: manifest.id.clone(),
            storage: store.plugin_storage(&manifest.id),
            http: http.clone(),
            base_version: env!("CARGO_PKG_VERSION").to_string(),
            dir: store.plugin_dir(&manifest.id),
            logger: PluginLogger::new(&manifest.id),
            core: std::sync::Arc::new(channelflow_plugin_api::core::NoCoreData::default()),
            database: std::sync::Arc::new(channelflow_plugin_api::database::NoPluginDatabase::default()),
        };
        manager.add(plugin, api).await.expect("load AI plugin");
        manager.enable(&manifest.id).await.expect("enable AI plugin");
        let routers = manager.routers();
        let plugins = Arc::new(Mutex::new(manager));

        let about = AboutInfo {
            config_folder: dir.display().to_string(),
            listen_port: 0,
            started: std::time::Instant::now(),
        };
        let store_client = Arc::new(StoreClient::new(http.clone(), "http://localhost/manifest.json".to_string()));
        let _ = router(store, about, plugins, routers, store_client);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
