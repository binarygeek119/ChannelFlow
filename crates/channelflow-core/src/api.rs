//! HTTP surface: the JSON API for channels and plugins plus the static web UI
//! shell. Plugins contribute their own routers, nested under `/api/plugins`.

use std::sync::Arc;

use axum::{
    extract::{Path, Query, State},
    http::StatusCode,
    response::{IntoResponse, Response},
    routing::{delete, get, post, put},
    Json, Router,
};
use serde_json::json;
use tokio::sync::Mutex;
use uuid::Uuid;

use crate::model::{Channel, NewChannel, UpdateChannel};
use crate::plugin::{repo, PluginManager};
use crate::store::{Store, StoreError};

/// Facts about the running process that only `main` can know: where the config
/// directory is, what port is bound, and when the process started.
#[derive(Clone)]
pub struct AboutInfo {
    pub config_folder: String,
    pub listen_port: u16,
    pub started: std::time::Instant,
}

/// What handlers may reach: the channel store, the process facts above, the
/// loaded plugins, and the outbound HTTP client repository installs use. The
/// same client is what each plugin's own state holds.
#[derive(Clone)]
pub struct AppState {
    pub store: Store,
    pub about: AboutInfo,
    pub plugins: Arc<Mutex<PluginManager>>,
    pub http: reqwest::Client,
}

pub fn router(
    store: Store,
    about_info: AboutInfo,
    plugins: Arc<Mutex<PluginManager>>,
    http: reqwest::Client,
    plugin_routers: Vec<(String, Router)>,
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
        .route("/api/plugins/{id}/enable", put(enable_plugin))
        .route("/api/plugins/{id}/disable", put(disable_plugin))
        .route("/api/plugins/repositories", get(list_repositories).post(add_repository))
        .route("/api/plugins/repositories/{id}", delete(remove_repository))
        .route("/api/plugins/catalog", get(plugin_catalog))
        .route("/api/plugins/install", post(install_plugin))
        .route("/api/plugins/installed", get(list_installed))
        .route("/api/plugins/installed/{id}", delete(uninstall_plugin))
        .route("/live/{asset}", get(live_pending))
        .with_state(AppState {
            store,
            about: about_info,
            plugins,
            http,
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

/// The plugins page's catalog: every loaded plugin with its manifest,
/// requested permissions, declared UI contributions, and current health.
async fn list_plugins(
    State(state): State<AppState>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let manager = state.plugins.lock().await;
    Ok(Json(json!({ "plugins": manager.catalog() })))
}

async fn enable_plugin(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let mut manager = state.plugins.lock().await;
    manager
        .enable(&id)
        .await
        .map_err(|error| StoreError::Plugin(error.to_string()))?;
    Ok(Json(json!({ "plugins": manager.catalog() })))
}

async fn disable_plugin(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let mut manager = state.plugins.lock().await;
    manager
        .disable(&id)
        .await
        .map_err(|error| StoreError::Plugin(error.to_string()))?;
    Ok(Json(json!({ "plugins": manager.catalog() })))
}

// ── plugin repositories and the install catalog ─────────────────────────

/// The registered plugin-repository URLs, the Jellyfin-style install source.
async fn list_repositories(
    State(state): State<AppState>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let repositories = state.store.repo_list().await?;
    Ok(Json(json!({ "repositories": repositories })))
}

#[derive(serde::Deserialize)]
struct AddRepository {
    url: String,
}

/// Register a repository. The manifest is fetched once before the URL is
/// accepted, so a plain wrong URL cannot be saved.
async fn add_repository(
    State(state): State<AppState>,
    Json(input): Json<AddRepository>,
) -> Result<(StatusCode, Json<serde_json::Value>), ApiError> {
    let url = input.url.trim().to_string();
    if !(url.starts_with("https://") || url.starts_with("http://")) {
        return Err(StoreError::Plugin(
            "repository URL must start with http:// or https://".to_string(),
        )
        .into());
    }
    repo::fetch_catalog(&state.http, &url).await?;
    let repositories = state.store.repo_add(&url).await?;
    Ok((StatusCode::CREATED, Json(json!({ "repositories": repositories }))))
}

/// Forget a repository by its record id.
async fn remove_repository(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let repositories = state.store.repo_remove(&id).await?;
    Ok(Json(json!({ "repositories": repositories })))
}

#[derive(serde::Deserialize)]
struct CatalogQuery {
    url: Option<String>,
}

/// Everything installable across the registered repositories (or one
/// repository when `?url=` is given): each plugin with its versions, the rids
/// each version ships, and whether any version runs on this base. A
/// repository that cannot be fetched is reported in `errors`, not fatal.
async fn plugin_catalog(
    State(state): State<AppState>,
    Query(query): Query<CatalogQuery>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let base = env!("CARGO_PKG_VERSION");
    let urls: Vec<String> = match query.url {
        Some(url) => vec![url],
        None => state
            .store
            .repo_list()
            .await?
            .iter()
            .filter_map(|repo| repo["url"].as_str().map(str::to_string))
            .collect(),
    };
    let mut plugins = Vec::new();
    let mut errors = Vec::new();
    for url in urls {
        match repo::fetch_catalog(&state.http, &url).await {
            Ok(entries) => {
                let view = repo::catalog_view(&url, &entries, base);
                if let Some(list) = view["plugins"].as_array() {
                    plugins.extend(list.iter().cloned());
                }
            }
            Err(error) => {
                tracing::warn!(repository = %url, error = %error, "repository catalog fetch failed");
                errors.push(json!({ "repository": url, "error": error.to_string() }));
            }
        }
    }
    Ok(Json(json!({ "plugins": plugins, "errors": errors })))
}

#[derive(serde::Deserialize)]
struct InstallRequest {
    /// The repository URL the plugin comes from.
    url: String,
    /// The manifest's plugin id, e.g. `com.channelflow.ai`.
    id: String,
    /// Pin a version; when absent the newest compatible version is installed.
    version: Option<String>,
    /// Override the host's platform rid, for custom staging.
    rid: Option<String>,
}

/// Download, verify, and stage one plugin from a repository. The library is
/// not loaded — that is the dynamic loader's job once the SDK defines its ABI.
async fn install_plugin(
    State(state): State<AppState>,
    Json(input): Json<InstallRequest>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let rid = input.rid.unwrap_or_else(repo::current_rid);
    let installed = repo::install(
        &state.http,
        &state.store,
        &input.url,
        &input.id,
        input.version.as_deref(),
        &rid,
        env!("CARGO_PKG_VERSION"),
    )
    .await?;
    Ok(Json(json!({ "installed": installed, "rid": rid })))
}

/// The installs on disk, one record per plugin id.
async fn list_installed(
    State(state): State<AppState>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let installed = state.store.installed_list().await?;
    Ok(Json(json!({ "installed": installed })))
}

/// Remove an installed plugin (its directory and record). Compiled-in plugins
/// keep working either way.
async fn uninstall_plugin(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let removed = repo::uninstall(&state.store, &id).await?;
    Ok(Json(json!({ "removed": removed })))
}

/// The streaming paths the Live TV page advertises.
///
/// Only port `8097` is published and the encoder listens inside the container
/// on another port, so every URL the page shows is written against ChannelFlow's
/// own origin. Until the playout milestone produces those streams these paths
/// answer `503` with a sentence rather than `404`: a player pointed at one gets
/// an honest "not yet" instead of "no such thing", and the milestone repoints
/// these at ErsatzTV next without the page changing.
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
        let _ = router(store, about, plugins, http, routers);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
