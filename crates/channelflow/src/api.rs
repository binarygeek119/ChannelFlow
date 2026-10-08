//! HTTP surface: the JSON API for channels plus the static web UI shell.

use axum::{
    extract::{Path, State},
    http::StatusCode,
    response::{IntoResponse, Response},
    routing::{get, post},
    Json, Router,
};
use serde_json::json;
use uuid::Uuid;

use crate::ai::AiView;
use crate::model::{Channel, NewChannel, UpdateChannel};
use crate::openai;
use crate::store::{Store, StoreError};
use crate::transcode::{self, TranscodeConfig};

/// Facts about the running process that only `main` can know: where the config
/// directory is, what port is bound, and when the process started.
#[derive(Clone)]
pub struct AboutInfo {
    pub config_folder: String,
    pub listen_port: u16,
    pub started: std::time::Instant,
}

/// What handlers may reach: the channel store, the process facts above, and
/// the one HTTP client outbound requests share. Kept as one type so the next
/// milestone (playout state) extends this rather than adding a second state
/// type to route around.
#[derive(Clone)]
pub struct AppState {
    pub store: Store,
    pub about: AboutInfo,
    pub http: reqwest::Client,
}

pub fn router(store: Store, about_info: AboutInfo, http: reqwest::Client) -> Router {
    Router::new()
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
        .route("/api/ai", get(get_ai).put(put_ai))
        .route("/api/ai/test", post(test_ai))
        .route("/api/transcode", get(get_transcode).put(put_transcode))
        .route(
            "/api/channels/{id}/transcode",
            get(get_channel_transcode)
                .put(put_channel_transcode)
                .delete(clear_channel_transcode),
        )
        .route("/live/{asset}", get(live_pending))
        .with_state(AppState {
            store,
            about: about_info,
            http,
        })
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
            StoreError::Transcode(_) => (StatusCode::BAD_REQUEST, self.0.to_string()),
            StoreError::Ai(_) => (StatusCode::BAD_REQUEST, self.0.to_string()),
            StoreError::Io(_) | StoreError::Json(_) => {
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
    Ok(Json(state.store.list()?))
}

async fn get_channel(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
) -> Result<Json<Channel>, ApiError> {
    Ok(Json(state.store.get(id)?))
}

async fn create_channel(
    State(state): State<AppState>,
    Json(input): Json<NewChannel>,
) -> Result<(StatusCode, Json<Channel>), ApiError> {
    let channel = state.store.create(input)?;
    Ok((StatusCode::CREATED, Json(channel)))
}

async fn update_channel(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
    Json(input): Json<UpdateChannel>,
) -> Result<Json<Channel>, ApiError> {
    Ok(Json(state.store.update(id, input)?))
}

async fn delete_channel(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, ApiError> {
    state.store.delete(id)?;
    Ok(StatusCode::NO_CONTENT)
}

/// The AI page's settings. The saved key is never returned — only whether one
/// is set — and an update that omits it keeps the stored one.
async fn get_ai(State(state): State<AppState>) -> Result<Json<AiView>, ApiError> {
    Ok(Json(state.store.ai_config()?.view()))
}

async fn put_ai(
    State(state): State<AppState>,
    Json(body): Json<serde_json::Value>,
) -> Result<Json<AiView>, ApiError> {
    let current = state.store.ai_config()?;
    let updated = current.apply(&body).map_err(StoreError::Ai)?;
    state.store.save_ai_config(&updated)?;
    Ok(Json(updated.view()))
}

/// Try the endpoint now, with the values on the page rather than the stored
/// ones, so a key or a voice can be checked before it is saved. Nothing is
/// written, and a failed connection is a normal result — `200` with `ok`.
async fn test_ai(
    State(state): State<AppState>,
    Json(body): Json<serde_json::Value>,
) -> Result<Json<openai::Report>, ApiError> {
    let config = state.store.ai_config()?.apply(&body).map_err(StoreError::Ai)?;
    Ok(Json(openai::run(&state.http, &config).await))
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

/// The instance transcode defaults, together with the field list the page
/// renders.
///
/// The spec travels with the values so the form cannot drift from next's
/// schema: `transcode::spec` is checked against
/// `vendor/ersatztv-next/schema/channel_config.json` by a test in that module.
async fn get_transcode(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    let defaults = state.store.transcode_defaults()?;
    Ok(Json(json!({
        "spec": transcode::spec(),
        "defaults": defaults,
    })))
}

async fn put_transcode(
    State(state): State<AppState>,
    Json(body): Json<serde_json::Value>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let config = TranscodeConfig::parse(&body).map_err(StoreError::Transcode)?;
    state.store.save_transcode_defaults(&config)?;
    Ok(Json(json!({ "defaults": config })))
}

/// A channel's overrides plus the effective settings they resolve to, so the
/// dialog can show both "you set this" and "this is what next will do".
async fn get_channel_transcode(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let channel = state.store.get(id)?;
    let defaults = state.store.transcode_defaults()?;
    let effective = defaults
        .merged(&channel.transcode)
        .map_err(StoreError::Transcode)?;
    Ok(Json(json!({
        "channel": { "id": channel.id, "number": channel.number, "name": channel.name },
        "spec": transcode::spec(),
        "defaults": defaults,
        "overrides": channel.transcode,
        "effective": effective,
    })))
}

async fn put_channel_transcode(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
    Json(body): Json<serde_json::Value>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let defaults = state.store.transcode_defaults()?;
    // Resolve before storing, so a patch next would reject never reaches the
    // channel document.
    let effective = defaults.merged(&body).map_err(StoreError::Transcode)?;
    let channel = state.store.set_channel_transcode(id, body)?;
    Ok(Json(json!({
        "overrides": channel.transcode,
        "effective": effective,
    })))
}

/// Drop every override so the channel follows the Transcode page again.
async fn clear_channel_transcode(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let defaults = state.store.transcode_defaults()?;
    let channel = state.store.set_channel_transcode(
        id,
        serde_json::Value::Object(serde_json::Map::new()),
    )?;
    Ok(Json(json!({
        "overrides": channel.transcode,
        "effective": defaults,
    })))
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

    /// The router is built once at startup, so a malformed path — a segment
    /// that mixes a parameter with a literal like `/live/{n}.m3u8`, say —
    /// panics there instead of failing a request. Building it here turns that
    /// class of mistake into a test failure rather than a server that will not
    /// boot.
    #[test]
    fn the_router_builds() {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("channelflow-router-{stamp}"));
        let store = Store::open(&dir).expect("open store");
        let about = AboutInfo {
            config_folder: dir.display().to_string(),
            listen_port: 0,
            started: std::time::Instant::now(),
        };
        let http = crate::openai::client().expect("http client");
        let _ = router(store, about, http);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
