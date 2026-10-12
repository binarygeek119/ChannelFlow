//! HTTP surface: the JSON API for channels and plugins plus the static web UI
//! shell. Plugins contribute their own routers, nested under `/api/plugins`.

use std::sync::Arc;

use axum::{
    extract::{Path, Query, Request, State},
    http::{header, HeaderMap, HeaderValue, Method, StatusCode, Uri},
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::{delete, get, post, put},
    Json, Router,
};
use axum::extract::DefaultBodyLimit;
use serde::Deserialize;
use serde_json::json;
use tokio::sync::Mutex;
use uuid::Uuid;

use crate::auth::{self, AuthRecord, ResetPin};
use crate::media::MediaSources;
use crate::model::{Channel, NewChannel, UpdateChannel};
use crate::plugin::{repo, PluginManager};
use crate::store::{Store, StoreError};
use crate::tasks;

use base64::Engine as _;

/// Facts about the running process that only `main` can know: where the config
/// directory is, what port is bound, and when the process started.
#[derive(Clone)]
pub struct AboutInfo {
    pub config_folder: String,
    pub listen_port: u16,
    pub started: std::time::Instant,
}

/// What handlers may reach: the channel store, the process facts above, the
/// loaded plugins, the registered media sources, and the outbound HTTP client
/// repository installs use. The same client is what each plugin's own state
/// holds.
#[derive(Clone)]
pub struct AppState {
    pub store: Store,
    pub about: AboutInfo,
    pub plugins: Arc<Mutex<PluginManager>>,
    pub media: Arc<MediaSources>,
    pub http: reqwest::Client,
    pub encoder: Arc<crate::encoder::Encoder>,
}

pub fn router(
    store: Store,
    about_info: AboutInfo,
    plugins: Arc<Mutex<PluginManager>>,
    media: Arc<MediaSources>,
    http: reqwest::Client,
    encoder: Arc<crate::encoder::Encoder>,
    plugin_routers: Vec<(String, Router)>,
) -> Router {
    let state = AppState {
        store,
        about: about_info,
        plugins,
        media,
        http,
        encoder,
    };

    let mut app = Router::new()
        .route("/", get(index))
        .route("/first-time", get(index))
        .route("/app.css", get(css))
        .route("/app.js", get(js))
        .route("/pages/{*path}", get(page_asset))
        .route("/plugin/{id}/web/{*path}", get(plugin_web_asset))
        .route("/logos/{name}", get(logo_badge))
        .route("/logo.png", get(logo))
        .route("/favicon.ico", get(favicon))
        .route("/favicon-32x32.png", get(favicon_32))
        .route("/favicon-16x16.png", get(favicon_16))
        .route("/apple-touch-icon.png", get(apple_touch_icon))
        .route("/api/about", get(about))
        .route("/api/health", get(health))
        .route("/api/settings/general", get(general_settings_get).put(general_settings_put))
        .route("/api/settings/password", post(change_password))
        .route("/api/quickpin", get(quickpin_get))
        .route("/api/quickpin/pair", post(quickpin_pair))
        .route("/api/clients", get(list_clients))
        .route("/api/clients/{id}", delete(remove_client))
        .route(
            "/api/client-logs",
            get(list_client_logs)
                .post(ingest_client_logs)
                .layer(DefaultBodyLimit::max(524_288)),
        )
        .route("/api/client-logs/{device_id}", get(get_client_logs_device))
        .route("/api/iptv/urls", get(iptv_urls))
        .route("/api/guide", get(tv_guide))
        .route("/api/auth/state", get(auth_state))
        .route("/api/auth/login", post(login))
        .route("/api/auth/logout", post(logout))
        .route("/api/auth/setup", post(setup))
        .route("/api/auth/forgot", post(forgot))
        .route("/api/auth/reset", post(reset_password))
        .route("/api/setup/database", post(setup_database))
        .route(
            "/api/channels",
            get(list_channels)
                .post(create_channel)
                .layer(DefaultBodyLimit::max(8 * 1024 * 1024)),
        )
        .route(
            "/api/channels/{id}",
            get(get_channel)
                .put(update_channel)
                .delete(delete_channel)
                .layer(DefaultBodyLimit::max(8 * 1024 * 1024)),
        )
        .route("/api/plugins", get(list_plugins))
        .route("/api/plugins/{id}/update", put(update_plugin))
        .route("/api/plugins/{id}/enable", put(enable_plugin))
        .route("/api/plugins/{id}/disable", put(disable_plugin))
        .route("/api/plugins/repositories", get(list_repositories).post(add_repository))
        .route("/api/plugins/repositories/{id}", delete(remove_repository))
        .route("/api/plugins/catalog", get(plugin_catalog))
        .route("/api/plugins/install", post(install_plugin))
        .route("/api/plugins/installed", get(list_installed))
        .route("/api/plugins/installed/{id}", delete(uninstall_plugin))
        .route("/api/mediasources", get(list_media_sources))
        .route("/api/connections", get(list_connections).post(create_connection))
        .route("/api/connections/{id}", put(update_connection).delete(delete_connection))
        .route("/api/connections/{id}/test", post(test_connection))
        .route(
            "/api/tasks/jellyfin-sync",
            get(jellyfin_sync_get).put(jellyfin_sync_put),
        )
        .route("/api/tasks/jellyfin-sync/run", post(jellyfin_sync_run))
        .route(
            "/api/tasks/news-scan",
            get(news_scan_get).put(news_scan_put),
        )
        .route("/api/tasks/news-scan/run", post(news_scan_run))
        .route(
            "/api/tasks/local-scan",
            get(local_scan_get).put(local_scan_put),
        )
        .route("/api/tasks/local-scan/run", post(local_scan_run))
        .route("/api/tasks/running", get(tasks_running))
        .route("/api/playout/regenerate", post(regenerate_playout))
        .route("/api/media", get(media_catalog_list))
        .route("/api/media/source-index", get(media_source_index))
        .route("/api/media/tv/seasons", get(tv_seasons))
        .route("/api/media/tv/episodes", get(tv_episodes))
        .route("/api/media/tv/episode-image", get(tv_episode_image))
        .route("/api/media/music/artists", get(music_artists))
        .route("/api/media/music/albums", get(music_albums))
        .route("/api/media/music/videos", get(music_videos))
        .route("/api/media/music/albums/{id}/tracks", get(music_album_tracks))
        .route("/api/media/{match_key}", get(media_catalog_item))
        .route("/api/media/image", get(media_catalog_image))
        .route("/api/lineups", get(list_lineups))
        .route("/api/lineups/{id}", get(get_lineup).put(put_lineup))
        .route(
            "/api/lineups/{id}/overrides",
            post(create_lineup_override),
        )
        .route(
            "/api/lineups/overrides/{override_id}",
            put(update_lineup_override).delete(delete_lineup_override),
        )
        .route("/api/lineups/{id}/preview", post(preview_lineup))
        .route("/live/session/{channel}/{file}", get(live_segment))
        .route("/live/{asset}", get(live_asset))
        .fallback(spa_fallback)
        .with_state(state.clone());
    for (id, plugin_router) in plugin_routers {
        app = app.nest(&format!("/api/plugins/{id}"), plugin_router);
    }
    app.layer(middleware::from_fn_with_state(state.clone(), gate_setup))
        .layer(middleware::from_fn_with_state(state, require_auth))
        .layer(middleware::from_fn(no_store))
}

/// The walkthrough runs once. After that the server itself refuses to serve
/// it: `/` and `/first-time/*` redirect to `/webui/guide`. Before setup, the
/// app URLs redirect to `/first-time`. The page must not make this choice —
/// a stale script doing it is what looped people back to the walkthrough.
async fn gate_setup(State(state): State<AppState>, request: Request, next: Next) -> Response {
    let path = request.uri().path().to_string();
    if let Some(response) = setup_redirect(&state, &path).await {
        return response;
    }
    next.run(request).await
}

/// Where this path belongs, if it doesn't belong where it was requested.
/// `None` means serve the page. Setup complete never serves the walkthrough;
/// setup incomplete never serves the app.
async fn setup_redirect(state: &AppState, path: &str) -> Option<Response> {
    if path.starts_with("/api/") || is_static_asset(path) {
        return None;
    }
    let setup_done = setup_is_done(state).await;
    let walkthrough = path == "/first-time" || path.starts_with("/first-time/");
    let app = path == "/webui" || path.starts_with("/webui/");
    if setup_done && (walkthrough || path == "/" || path == "/webui" || path == "/webui/") {
        return Some(redirect_to("/webui/guide"));
    }
    if !setup_done && (app || path == "/") {
        return Some(redirect_to("/first-time"));
    }
    None
}

async fn setup_is_done(state: &AppState) -> bool {
    if state.store.has_setup_marker() {
        return true;
    }
    match state.store.auth_record().await {
        Ok(Some(record)) if record.setup_complete => {
            let _ = state.store.write_setup_marker();
            true
        }
        _ => false,
    }
}

fn is_static_asset(path: &str) -> bool {
    matches!(
        path,
        "/app.css"
            | "/app.js"
            | "/logo.png"
            | "/favicon.ico"
            | "/favicon-32x32.png"
            | "/favicon-16x16.png"
            | "/apple-touch-icon.png"
    ) || path.starts_with("/live/")
        || path.starts_with("/logos/")
        || path.starts_with("/plugin/")
}

fn redirect_to(path: &str) -> Response {
    // 302, not 307. A 307 can be cached, and a cached "go to the walkthrough"
    // redirect is exactly the loop that kept sending finished installs back
    // to /first-time/welcome.
    Response::builder()
        .status(StatusCode::FOUND)
        .header(header::LOCATION, path)
        .header(
            header::CACHE_CONTROL,
            "no-store, no-cache, must-revalidate, max-age=0",
        )
        .header(header::PRAGMA, "no-cache")
        .header(header::EXPIRES, "0")
        .body(axum::body::Body::empty())
        .unwrap_or_else(|_| StatusCode::FOUND.into_response())
}

/// SPA fallback: any path that isn't the JSON API serves the UI document, so
/// every app tab and every walkthrough step is deep-linkable. This is also
/// where `/first-time/welcome` and `/webui/guide` are decided — the layer
/// above does not run for the fallback.
async fn spa_fallback(State(state): State<AppState>, uri: Uri) -> Response {
    let path = uri.path();
    if path.starts_with("/api/") {
        return (StatusCode::NOT_FOUND, Json(json!({ "error": "not found" }))).into_response();
    }
    if let Some(response) = setup_redirect(&state, path).await {
        return response;
    }
    index(State(state)).await
}

/// API answers must never be cached: a stale `/api/auth/state` (say, from
/// before the database was reset) would otherwise keep a browser on the wrong
/// screen — login instead of the first-boot walkthrough.
async fn no_store(request: Request, next: Next) -> Response {
    let is_api = request.uri().path().starts_with("/api/");
    let mut response = next.run(request).await;
    if is_api {
        response
            .headers_mut()
            .insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    }
    response
}

fn session_cookie(headers: &axum::http::HeaderMap) -> Option<String> {
    headers
        .get(header::COOKIE)
        .and_then(|value| value.to_str().ok())
        .and_then(|cookie| {
            cookie.split(';').find_map(|part| {
                part.trim()
                    .strip_prefix("channelflow_session=")
                    .map(str::to_string)
            })
        })
}

/// Until setup finishes the API stays open so the walkthrough can connect the
/// database and install plugins. After setup every API call except the auth
/// endpoints and /api/health requires a real session - the login screen is
/// back.
async fn require_auth(State(state): State<AppState>, request: Request, next: Next) -> Response {
    let path = request.uri().path();
    if !path.starts_with("/api/")
        || path.starts_with("/api/auth/")
        || path == "/api/health"
        || path.starts_with("/api/setup/")
        || (path == "/api/client-logs" && request.method() == Method::POST)
    {
        return next.run(request).await;
    }
    if !setup_is_done(&state).await {
        return next.run(request).await;
    }
    let cookie = session_cookie(request.headers());
    let valid = match cookie {
        Some(cookie) => state.store.session_valid(&cookie).await.unwrap_or(false),
        None => false,
    };
    if valid {
        next.run(request).await
    } else {
        (
            StatusCode::UNAUTHORIZED,
            Json(json!({ "error": "log in to use the web UI" })),
        )
            .into_response()
    }
}

// ── web UI auth ────────────────────────────────────────────────────────────

/// Whether setup is done and whether this request is logged in. Setup is the
/// same marker/record source the routing layer uses; authenticated reflects a
/// real session cookie, because the login screen is required after setup.
async fn auth_state(
    State(state): State<AppState>,
    headers: axum::http::HeaderMap,
) -> Result<Json<serde_json::Value>, ApiError> {
    let record = state.store.auth_record().await?;
    let setup_done = setup_is_done(&state).await;
    let cookie = session_cookie(&headers);
    let authenticated = match (setup_done, cookie) {
        (true, Some(cookie)) => state.store.session_valid(&cookie).await.unwrap_or(false),
        _ => false,
    };
    Ok(Json(json!({
        "setup_done": setup_done,
        "authenticated": authenticated,
        "username": record.map(|r| r.username),
    })))
}

/// Sessions last the full login-cookie lifetime (30 days) and slide forward on
/// every request, so there is no short idle timeout — a login stays valid as
/// long as the browser keeps the cookie. The remember-me box decides whether
/// the browser keeps the cookie across restarts; it does not shorten how long
/// a session lives.
const SESSION_TTL_HOURS: i64 = crate::store::SESSION_TTL_HOURS;
/// How long the login cookie lives in the browser (the default expiry).
const REMEMBER_COOKIE_SECS: u64 = 30 * 24 * 3600;

#[derive(Deserialize)]
struct LoginBody {
    username: String,
    password: String,
    #[serde(default)]
    remember: bool,
}

async fn login(
    State(state): State<AppState>,
    Json(input): Json<LoginBody>,
) -> Result<Response, ApiError> {
    let ok = state
        .store
        .auth_record()
        .await?
        .is_some_and(|record| {
            record.username == input.username.trim() && auth::verify(&record, &input.password)
        });
    if !ok {
        return Ok((
            StatusCode::UNAUTHORIZED,
            Json(json!({ "error": "wrong username or password" })),
        )
            .into_response());
    }
    let token = auth::new_secret();
    let expires_at = chrono::Utc::now() + chrono::Duration::hours(SESSION_TTL_HOURS);
    state
        .store
        .save_session(&token, expires_at, input.remember)
        .await?;
    // The cookie lasts the default 30 days either way; remember just decides
    // whether the browser keeps it across restarts.
    let cookie = format!(
        "channelflow_session={token}; Path=/; HttpOnly; SameSite=Lax; Max-Age={REMEMBER_COOKIE_SECS}"
    );
    Ok(([(header::SET_COOKIE, cookie)], Json(json!({ "ok": true }))).into_response())
}

async fn logout(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    state.store.clear_session().await?;
    Ok(Json(json!({ "ok": true })))
}

#[derive(Deserialize)]
struct SetupBody {
    username: String,
    password: String,
}

/// Create the web UI's admin account and mark setup complete. From here on
/// every API call needs a session.
async fn setup(
    State(state): State<AppState>,
    Json(input): Json<SetupBody>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let username = input.username.trim().to_string();
    if username.is_empty() {
        return Err(StoreError::Plugin("username cannot be empty".to_string()).into());
    }
    if input.password.len() < 4 {
        return Err(StoreError::Plugin("password must be at least 4 characters".to_string()).into());
    }
    let salt = auth::new_secret();
    let record = AuthRecord {
        username,
        salt: salt.clone(),
        password_hash: auth::hash_password(&salt, &input.password),
        setup_complete: true,
    };
    state.store.save_auth_record(&record).await?;
    // The walkthrough is a one-time thing. This file is what later page loads
    // check first, so a completed install is never sent back through setup.
    state.store.write_setup_marker()?;
    Ok(Json(json!({ "ok": true })))
}

#[derive(Deserialize)]
struct DatabaseBody {
    /// The Postgres connection string, e.g. postgres://user:pass@host:5432/db.
    url: String,
}

/// The walkthrough's database step: connect to the Postgres the user entered,
/// make the schema, import anything that currently lives in the config
/// directory, and switch the running store to it. The URL is only persisted
/// after the connect succeeds, so a bad string cannot lock a restart out.
async fn setup_database(
    State(state): State<AppState>,
    Json(input): Json<DatabaseBody>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let url = input.url.trim().to_string();
    if !(url.starts_with("postgres://") || url.starts_with("postgresql://")) {
        return Err(StoreError::Plugin(
            "the connection string must look like postgres://user:password@host:5432/db".to_string(),
        )
        .into());
    }
    if let Err(error) = state.store.connect_database(&url).await {
        tracing::warn!(%error, "the database step could not connect");
        return Err(StoreError::Plugin(format!("Postgres: {error}")).into());
    }
    state.store.save_database_url(&url).await?;
    Ok(Json(json!({ "ok": true, "database": "postgres" })))
}

/// How long is a password-reset pin valid for, and the cooldown between
/// resets.
const RESET_COOLDOWN_SECONDS: i64 = 600;
const RESET_PIN_TTL_SECONDS: i64 = 1800;

/// Start a password reset: write a fresh random pin to a file in the config
/// directory (`reset-<MM-DD-YY-HH-MM-SS>.txt`) that only someone with
/// filesystem access can read. Nothing about the pin goes through the web UI.
async fn forgot(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    let now = chrono::Utc::now();
    if let Some(last) = state.store.last_reset_at().await? {
        let elapsed = now - last;
        if elapsed <= chrono::Duration::seconds(RESET_COOLDOWN_SECONDS) {
            let wait = RESET_COOLDOWN_SECONDS - elapsed.num_seconds();
            return Err(StoreError::Plugin(format!(
                "a reset is still on cooldown — try again in {wait} seconds"
            ))
            .into());
        }
    }

    let pin = auth::generate_pin();
    let filename = format!("reset-{}.txt", chrono::Local::now().format("%m-%d-%y-%H-%M-%S"));
    let path = state.store.config_dir().join(&filename);
    std::fs::write(&path, format!("ChannelFlow password reset pin\n\npin: {pin}\n"))
        .map_err(StoreError::Io)?;

    state
        .store
        .save_reset_pin(&ResetPin {
            pin: pin.clone(),
            file: filename.clone(),
            created_at: now,
        })
        .await?;
    state.store.save_last_reset_at(now).await?;
    tracing::info!(path = %path.display(), "wrote a password reset pin — only readable from the config directory");
    Ok(Json(json!({ "file": filename })))
}

#[derive(Deserialize)]
struct ResetBody {
    pin: String,
    password: String,
}

/// Finish a password reset: match the pin from the config-directory file,
/// then set the new password. The file and the stored pin are cleared.
async fn reset_password(
    State(state): State<AppState>,
    Json(input): Json<ResetBody>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let active = state
        .store
        .reset_pin()
        .await?
        .ok_or_else(|| StoreError::Plugin("no reset is pending — generate a pin first".to_string()))?;
    let age = chrono::Utc::now() - active.created_at;
    if age > chrono::Duration::seconds(RESET_PIN_TTL_SECONDS) {
        // Clear it so a stale pin cannot be tried forever.
        let _ = state.store.clear_reset_pin().await;
        return Err(StoreError::Plugin(
            "that reset pin has expired — generate a new one".to_string(),
        )
        .into());
    }
    if !auth::verify_token(&active.pin, input.pin.trim()) {
        return Err(StoreError::Plugin(
            "that pin does not match the one in the reset file".to_string(),
        )
        .into());
    }
    if input.password.len() < 4 {
        return Err(StoreError::Plugin(
            "password must be at least 4 characters".to_string(),
        )
        .into());
    }
    let mut record = state
        .store
        .auth_record()
        .await?
        .ok_or_else(|| StoreError::Plugin("there is no account to reset".to_string()))?;
    let salt = auth::new_secret();
    record.salt = salt.clone();
    record.password_hash = auth::hash_password(&salt, &input.password);
    state.store.save_auth_record(&record).await?;

    let _ = std::fs::remove_file(state.store.config_dir().join(&active.file));
    state.store.clear_reset_pin().await?;
    state.store.save_last_reset_at(chrono::Utc::now()).await?;
    // Any open session is old credentials; revoke it after a reset.
    state.store.clear_session().await?;
    Ok(Json(json!({ "ok": true })))
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

// ── general settings ───────────────────────────────────────────────────────

/// The stored public/local URLs. The local URL is detected once — on first boot,
/// from the address this request arrived on (falling back to the host's primary
/// interface) — and never guessed again; the operator can override it here.
async fn general_settings_get(
    State(state): State<AppState>,
    headers: HeaderMap,
) -> Result<Json<serde_json::Value>, ApiError> {
    let mut settings = state.store.general_settings().await?;
    if settings.local_url.is_empty() {
        let host = headers
            .get(header::HOST)
            .and_then(|value| value.to_str().ok());
        if let Some(url) = crate::settings::detect_local_url(state.about.listen_port, host) {
            settings.local_url = url;
            state.store.save_general_settings(&settings).await?;
        }
    }
    Ok(Json(json!({ "settings": settings })))
}

async fn general_settings_put(
    State(state): State<AppState>,
    Json(input): Json<crate::settings::GeneralSettings>,
) -> Result<Json<serde_json::Value>, ApiError> {
    if !crate::settings::is_valid_url(&input.public_url) {
        return Err(StoreError::Invalid("the public URL must start with http:// or https://").into());
    }
    if !crate::settings::is_valid_url(&input.local_url) {
        return Err(StoreError::Invalid("the local URL must start with http:// or https://").into());
    }
    let settings = input.normalized();
    state.store.save_general_settings(&settings).await?;
    Ok(Json(json!({ "settings": settings })))
}

// ── password ───────────────────────────────────────────────────────────────

#[derive(Deserialize)]
struct ChangePassword {
    old_password: String,
    new_password: String,
}

/// Change the logged-in account's password: the current one must match, then a
/// fresh salt and hash are stored. The session token is left as-is.
async fn change_password(
    State(state): State<AppState>,
    Json(input): Json<ChangePassword>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let mut record = state
        .store
        .auth_record()
        .await?
        .ok_or_else(|| StoreError::Plugin("there is no account yet".to_string()))?;
    if !auth::verify(&record, &input.old_password) {
        return Err(StoreError::Plugin("the current password is not correct".to_string()).into());
    }
    if input.new_password.len() < 4 {
        return Err(
            StoreError::Plugin("the new password must be at least 4 characters".to_string()).into(),
        );
    }
    auth::set_password(&mut record, &input.new_password);
    state.store.save_auth_record(&record).await?;
    Ok(Json(json!({ "ok": true })))
}

// ── quick pin ──────────────────────────────────────────────────────────────

/// The relay origin: the fixed default, overridable only server-side with
/// `CHANNELFLOW_PIN_SERVER` (the Quick Pin page cannot change it).
fn resolve_pin_server() -> String {
    std::env::var("CHANNELFLOW_PIN_SERVER")
        .ok()
        .map(|server| server.trim().to_string())
        .filter(|server| !server.is_empty())
        .unwrap_or_else(|| crate::quickpin::DEFAULT_SERVER.to_string())
}

/// The Quick Pin page's starting state: the relay origin and the Live TV URLs
/// that pairing would send (so the admin can see them first).
async fn quickpin_get(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    let settings = state.store.general_settings().await?;
    let urls = crate::quickpin::payload(&settings, true, "");
    Ok(Json(json!({
        "server": resolve_pin_server(),
        "urls": urls,
    })))
}

#[derive(Deserialize)]
struct QuickPinPair {
    pin: String,
}

/// Encrypt this instance's Live TV URLs with the app's PIN and hand the
/// ciphertext to the relay. The app is paired with its own fresh random API
/// key, which the URLs carry. `404` from the relay means the PIN was unknown,
/// expired, or already used — the just-issued key is then thrown away.
async fn quickpin_pair(
    State(state): State<AppState>,
    headers: HeaderMap,
    Json(input): Json<QuickPinPair>,
) -> Result<Json<serde_json::Value>, ApiError> {
    if !crate::quickpin::is_valid_pin(&input.pin) {
        return Err(StoreError::Invalid("enter the 8-character PIN shown on the app").into());
    }
    let server = resolve_pin_server();
    let settings = state.store.general_settings().await?;
    if settings.local_url.trim().is_empty() && settings.public_url.trim().is_empty() {
        return Err(StoreError::Invalid(
            "set a local or public URL on General Settings before pairing",
        )
        .into());
    }
    let host = headers.get(header::HOST).and_then(|value| value.to_str().ok());
    let primary_local = crate::quickpin::primary_is_local(&settings, host);
    let client = state.store.client_issue("Paired app").await?;
    let api_key = client["api_key"].as_str().unwrap_or("").to_string();
    let payload = crate::quickpin::payload(&settings, primary_local, &api_key);
    let plaintext = serde_json::to_vec(&payload).map_err(StoreError::Json)?;
    let ciphertext = crate::quickpin::encrypt(&input.pin, &plaintext)
        .map_err(|error| StoreError::Plugin(format!("could not encrypt the pairing details: {error}")))?;
    let delivered = crate::quickpin::deliver(&state.http, &server, &input.pin, &ciphertext)
        .await
        .map_err(StoreError::Plugin)?;
    if !delivered {
        // No app was waiting: drop the key we just minted for it.
        let id = client["id"].as_str().unwrap_or("").to_string();
        let _ = state.store.client_remove(&id).await;
    }
    Ok(Json(json!({ "delivered": delivered, "server": server })))
}

/// A masked API key: enough to tell two apart, never the whole secret.
fn key_hint(key: &str) -> String {
    if key.len() <= 10 {
        return "••••".to_string();
    }
    format!("{}…{}", &key[..6], &key[key.len() - 4..])
}

/// The paired clients, newest first, with only key hints (never full keys).
async fn list_clients(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    let mut clients = state.store.client_list().await?;
    clients.sort_by(|a, b| {
        b["created_at"]
            .as_str()
            .unwrap_or("")
            .cmp(a["created_at"].as_str().unwrap_or(""))
    });
    let list: Vec<serde_json::Value> = clients
        .iter()
        .map(|client| {
            json!({
                "id": client["id"],
                "label": client["label"],
                "key_hint": key_hint(client["api_key"].as_str().unwrap_or("")),
                "created_at": client["created_at"],
                "last_seen_at": client["last_seen_at"],
            })
        })
        .collect();
    let server_hint = key_hint(&state.store.server_api_key().await?);
    Ok(Json(json!({
        "clients": list,
        "server_key_hint": server_hint,
    })))
}

/// Revoke a paired client; its API key stops working at once.
async fn remove_client(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Response, ApiError> {
    if state.store.client_remove(&id).await? {
        Ok(Json(json!({ "ok": true })).into_response())
    } else {
        Ok((StatusCode::NOT_FOUND, Json(json!({ "error": "no such client" }))).into_response())
    }
}

/// Issue a fresh random key and return the Live TV URLs that carry it. Each
/// call is a new key, so every copied playlist URL is its own revocable client.
async fn iptv_urls(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    let client = state.store.client_issue("Playlist URL").await?;
    let api_key = client["api_key"].as_str().unwrap_or("").to_string();
    let settings = state.store.general_settings().await?;
    let urls = crate::quickpin::payload(&settings, true, &api_key);
    Ok(Json(json!({
        "api_key": api_key,
        "client_id": client["id"],
        "urls": urls,
    })))
}

// ── TV guide ──────────────────────────────────────────────────────────────

#[derive(Deserialize)]
struct GuideQuery {
    /// Window start (UTC ISO). When set, `hours` (1–24) is applied from here.
    from: Option<String>,
    /// A calendar day (`yyyy-MM-dd`) in the guide time zone; full day window.
    date: Option<String>,
    #[serde(default)]
    hours: Option<i64>,
}

/// A channel/time TV guide built from the enabled channels and whatever
/// programme blocks scheduling has written (the JSON TV guide shape v1.0.0's
/// GuideController served).
async fn tv_guide(
    State(state): State<AppState>,
    Query(query): Query<GuideQuery>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let settings = state.store.general_settings().await?;
    let zone_name = if settings.timezone.trim().is_empty() {
        "UTC".to_string()
    } else {
        settings.timezone.trim().to_string()
    };
    let zone: chrono_tz::Tz = zone_name.parse().unwrap_or(chrono_tz::UTC);
    let now = chrono::Utc::now();

    let (from, to) = if let Some(from_raw) = &query.from {
        let start = chrono::DateTime::parse_from_rfc3339(from_raw)
            .map(|value| value.with_timezone(&chrono::Utc))
            .unwrap_or(now);
        let hours = query.hours.unwrap_or(6).clamp(1, 24);
        (start, start + chrono::Duration::hours(hours))
    } else {
        let day = match query.date.as_deref() {
            Some(date) if date.trim().len() == 10 => {
                chrono::NaiveDate::parse_from_str(date.trim(), "%Y-%m-%d")
                    .unwrap_or_else(|_| now.with_timezone(&zone).date_naive())
            }
            _ => now.with_timezone(&zone).date_naive(),
        };
        let midnight = day.and_hms_opt(0, 0, 0).expect("midnight is a valid clock time");
        let start_local = match midnight.and_local_timezone(zone) {
            chrono::offset::LocalResult::Single(value) => value,
            _ => midnight.and_utc().with_timezone(&zone),
        };
        let end_local = start_local + chrono::Duration::days(1);
        (
            start_local.with_timezone(&chrono::Utc),
            end_local.with_timezone(&chrono::Utc),
        )
    };

    let mut channels = state.store.list().await?;
    channels.retain(|channel| channel.enabled);
    channels.sort_by(|a, b| {
        a.number
            .partial_cmp(&b.number)
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    let channels_json: Vec<serde_json::Value> = channels
        .iter()
        .map(|channel| {
            json!({
                "id": channel.id.to_string(),
                "number": channel.number.to_string(),
                "name": channel.name,
                "content_type": "media",
            })
        })
        .collect();

    let guide = state.store.guide_get().await?;
    let stored = guide["programs"].as_array().cloned().unwrap_or_default();
    let mut programs = Vec::new();
    for program in stored {
        let start = program["start"]
            .as_str()
            .and_then(|value| chrono::DateTime::parse_from_rfc3339(value).ok())
            .map(|value| value.with_timezone(&chrono::Utc));
        let finish = program["finish"]
            .as_str()
            .and_then(|value| chrono::DateTime::parse_from_rfc3339(value).ok())
            .map(|value| value.with_timezone(&chrono::Utc));
        let (Some(start), Some(finish)) = (start, finish) else {
            continue;
        };
        if finish <= from || start >= to {
            continue;
        }
        let is_now = start <= now && finish > now;
        programs.push(json!({
            "id": program["id"],
            "channelId": program["channel_id"],
            "start": program["start"],
            "finish": program["finish"],
            "title": program["title"],
            "subTitle": program["sub_title"],
            "description": program["description"],
            "episode": program["episode"],
            "categories": program["categories"],
            "year": program["year"],
            "rating": program["rating"],
            "posterUrl": program["poster_url"],
            "isNow": is_now,
            "isVirtual": false,
        }));
    }

    Ok(Json(json!({
        "from": from.to_rfc3339(),
        "to": to.to_rfc3339(),
        "now": now.to_rfc3339(),
        "timeZone": zone_name,
        "channels": channels_json,
        "programs": programs,
    })))
}

#[derive(Deserialize)]
struct ClientLogsQuery {
    #[serde(rename = "apiKey")]
    apikey: Option<String>,
    #[serde(default)]
    file: Option<String>,
    #[serde(default)]
    tail: Option<i64>,
}

fn provided_api_key(headers: &HeaderMap, query: Option<&str>) -> String {
    headers
        .get("x-api-key")
        .and_then(|value| value.to_str().ok())
        .map(str::to_string)
        .or_else(|| query.map(str::to_string))
        .unwrap_or_default()
}

/// A TV app ships its log lines here. Authenticated with the app's API key
/// (`X-Api-Key` header or `?apiKey=`), not the admin session.
async fn ingest_client_logs(
    State(state): State<AppState>,
    headers: HeaderMap,
    Query(query): Query<ClientLogsQuery>,
    Json(input): Json<crate::client_logs::IngestRequest>,
) -> Response {
    let key = provided_api_key(&headers, query.apikey.as_deref());
    if !state.store.api_key_valid(&key).await.unwrap_or(false) {
        return (
            StatusCode::UNAUTHORIZED,
            Json(json!({ "error": "invalid API key" })),
        )
            .into_response();
    }
    let config_dir = std::path::PathBuf::from(&state.about.config_folder);
    match crate::client_logs::ingest(&config_dir, &input) {
        Ok(result) => Json(json!({ "accepted": result.accepted, "device_id": result.device_id }))
            .into_response(),
        Err(message) => (
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": message })),
        )
            .into_response(),
    }
}

/// The TV apps that have sent logs, newest first.
async fn list_client_logs(State(state): State<AppState>) -> Json<serde_json::Value> {
    let config_dir = std::path::PathBuf::from(&state.about.config_folder);
    Json(json!({ "devices": crate::client_logs::list_devices(&config_dir) }))
}

/// One app's log files and the tail of its log.
async fn get_client_logs_device(
    State(state): State<AppState>,
    Path(device_id): Path<String>,
    Query(query): Query<ClientLogsQuery>,
) -> Response {
    let config_dir = std::path::PathBuf::from(&state.about.config_folder);
    match crate::client_logs::device_detail(
        &config_dir,
        &device_id,
        query.file.as_deref(),
        query.tail,
    ) {
        Some(detail) => Json(detail).into_response(),
        None => (
            StatusCode::NOT_FOUND,
            Json(json!({ "error": "no logs for that client" })),
        )
            .into_response(),
    }
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
    // Store the channel first (it owns the id), then write the watermark file
    // and record its path with a follow-up update.
    let new_image = input.watermark.as_ref().and_then(|wm| wm.image_data.clone());
    let created = state.store.create(input).await?;
    if let Some(encoded) = new_image {
        let path = write_watermark_image(&state, created.id, &encoded)?;
        let mut watermark = created.watermark.unwrap_or_default();
        watermark.image_path = Some(path);
        let updated = state
            .store
            .update(
                created.id,
                UpdateChannel {
                    number: None,
                    name: None,
                    description: None,
                    enabled: None,
                    ai_prompt: None,
                    watermark: Some(Some(watermark)),
                },
            )
            .await?;
        return Ok((StatusCode::CREATED, Json(updated)));
    }
    Ok((StatusCode::CREATED, Json(created)))
}

async fn update_channel(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
    Json(input): Json<UpdateChannel>,
) -> Result<Json<Channel>, ApiError> {
    // A fresh upload replaces the stored art; a cleared watermark removes it.
    let new_image = input
        .watermark
        .as_ref()
        .and_then(|part| part.as_ref())
        .and_then(|wm| wm.image_data.clone());
    let clearing = matches!(input.watermark, Some(None));
    if new_image.is_some() || clearing {
        if let Ok(existing) = state.store.get(id).await {
            if let Some(old_path) = existing.watermark.as_ref().and_then(|wm| wm.image_path.as_ref()) {
                let _ = std::fs::remove_file(state.store.images_dir().join(old_path));
            }
        }
    }
    let updated = state.store.update(id, input).await?;
    if let Some(encoded) = new_image {
        let path = write_watermark_image(&state, id, &encoded)?;
        let mut watermark = updated.watermark.unwrap_or_default();
        watermark.image_path = Some(path);
        let updated = state
            .store
            .update(
                id,
                UpdateChannel {
                    number: None,
                    name: None,
                    description: None,
                    enabled: None,
                    ai_prompt: None,
                    watermark: Some(Some(watermark)),
                },
            )
            .await?;
        return Ok(Json(updated));
    }
    Ok(Json(updated))
}

/// Decode uploaded watermark bytes, reject anything that is not a PNG/JPEG,
/// and write them under `<images>/watermarks/{id}.<ext>`. Returns the
/// images-root-relative path the channel document records.
fn write_watermark_image(
    state: &AppState,
    id: Uuid,
    encoded: &str,
) -> Result<String, ApiError> {
    let encoded = encoded.split(',').next_back().unwrap_or(encoded).trim();
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(encoded)
        .map_err(|_| StoreError::Invalid("the watermark file is not valid base64"))?;
    if bytes.len() > 3 * 1024 * 1024 {
        return Err(StoreError::Invalid("the watermark file is larger than 3 MB").into());
    }
    let ext = if bytes.starts_with(b"\x89PNG\r\n\x1a\n") {
        "png"
    } else if bytes.starts_with(&[0xFF, 0xD8, 0xFF]) {
        "jpg"
    } else {
        return Err(StoreError::Invalid("the watermark must be a PNG or JPEG image").into());
    };
    let dir = state.store.images_dir().join("watermarks");
    std::fs::create_dir_all(&dir)
        .map_err(|error| StoreError::Plugin(format!("could not create the watermarks store: {error}")))?;
    let file = dir.join(format!("{id}.{ext}"));
    std::fs::write(&file, &bytes)
        .map_err(|error| StoreError::Plugin(format!("could not store the watermark: {error}")))?;
    Ok(format!("watermarks/{id}.{ext}"))
}

async fn delete_channel(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, ApiError> {
    if let Ok(existing) = state.store.get(id).await {
        if let Some(path) = existing
            .watermark
            .as_ref()
            .and_then(|wm| wm.image_path.clone())
        {
            let _ = std::fs::remove_file(state.store.images_dir().join(path));
        }
    }
    let _ = state.store.lineup_delete(id).await;
    state.store.delete(id).await?;
    Ok(StatusCode::NO_CONTENT)
}

// ── Lineups ────────────────────────────────────────────────────────────────
// The 1.0.0 schedule model ported into the base: a per-channel 48-slot daily
// roster with weighted candidates and day/week overrides. Playout is a later
// milestone; these routes own the roster document the editor edits and the
// playout pass will later read.

/// The Lineups page's channel picker: every channel plus whether it has a
/// saved roster and how much of the day its slots cover.
async fn list_lineups(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    let channels = state.store.list().await?;
    let mut rows = Vec::with_capacity(channels.len());
    for channel in channels {
        match state.store.lineup_get(channel.id).await? {
            Some(value) => {
                let (covered, has_lineup) = match serde_json::from_value::<crate::lineup::ChannelLineup>(value) {
                    Ok(lineup) => (
                        crate::lineup::validate_slots(&lineup.slots)
                            .ok()
                            .map(|slots| slots.iter().map(|s| s.span_slots).sum::<i32>())
                            .unwrap_or_default(),
                        true,
                    ),
                    Err(_) => (0, false),
                };
                rows.push(json!({
                    "id": channel.id,
                    "number": channel.number,
                    "name": channel.name,
                    "enabled": channel.enabled,
                    "has_lineup": has_lineup,
                    "slots_covered": covered,
                }));
            }
            None => rows.push(json!({
                "id": channel.id,
                "number": channel.number,
                "name": channel.name,
                "enabled": channel.enabled,
                "has_lineup": false,
                "slots_covered": 0,
            })),
        }
    }
    Ok(Json(json!({ "channels": rows })))
}

/// One channel's roster: the stored document, or a fresh empty default day.
async fn get_lineup(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
) -> Result<Json<serde_json::Value>, ApiError> {
    state.store.get(id).await.map_err(|_| api_not_found(id))?;
    let lineup = match state.store.lineup_get(id).await? {
        Some(value) => value,
        None => {
            let default = crate::lineup::ChannelLineup::default();
            serde_json::to_value(default).map_err(|error| api_bad_request(error.to_string()))?
        }
    };
    Ok(Json(json!({
        "lineup": lineup,
        "slots_per_day": crate::lineup::SLOTS_PER_DAY,
    })))
}

/// Replace the default roster. Body: `{ "slots": [...] }`. Overrides survive.
async fn put_lineup(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
    Json(value): Json<serde_json::Value>,
) -> Result<Json<serde_json::Value>, ApiError> {
    state.store.get(id).await.map_err(|_| api_not_found(id))?;
    let mut lineup = stored_lineup(&state, id).await?;
    let raw_slots = value.get("slots").cloned().unwrap_or_else(|| json!([]));
    let slots: Vec<crate::lineup::LineupSlot> = serde_json::from_value(raw_slots)
        .map_err(|error| api_bad_request(format!("slots are not valid: {error}")))?;
    lineup.slots = crate::lineup::validate_slots(&slots)
        .map_err(|error| api_bad_request(error.to_string()))?;
    let document = json!({ "slots": lineup.slots, "overrides": lineup.overrides });
    state.store.lineup_set(id, &document).await?;
    Ok(Json(json!({ "ok": true, "lineup": document })))
}

/// Body: `{ "name", "kind": "day_of_week"|"specific_date", "day_of_week"?
/// , "specific_date"?, "slots" }`. Creates an override; the default roster
/// stays untouched.
async fn create_lineup_override(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
    Json(body): Json<serde_json::Value>,
) -> Result<(StatusCode, Json<serde_json::Value>), ApiError> {
    state.store.get(id).await.map_err(|_| api_not_found(id))?;
    let mut lineup = stored_lineup(&state, id).await?;
    let mut override_ = parse_override(&body)?;
    override_.slots = crate::lineup::validate_slots(&override_.slots)
        .map_err(|error| api_bad_request(error.to_string()))?;
    lineup.overrides.push(override_.clone());
    let document = json!({ "slots": lineup.slots, "overrides": lineup.overrides });
    state.store.lineup_set(id, &document).await?;
    Ok((
        StatusCode::CREATED,
        Json(json!({ "ok": true, "override": override_, "lineup": document })),
    ))
}

/// Update one override by id; the roster is stored with the change.
async fn update_lineup_override(
    State(state): State<AppState>,
    Path(override_id): Path<Uuid>,
    Json(body): Json<serde_json::Value>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let (channel_id, slots, overrides) = find_override(&state, override_id).await?;
    let mut updated = parse_override(&body)?;
    updated.id = override_id;
    updated.slots = crate::lineup::validate_slots(&updated.slots)
        .map_err(|error| api_bad_request(error.to_string()))?;
    let overrides = overrides
        .into_iter()
        .map(|o| if o.id == override_id { updated.clone() } else { o })
        .collect::<Vec<_>>();
    let document = json!({ "slots": slots, "overrides": overrides });
    state.store.lineup_set(channel_id, &document).await?;
    Ok(Json(json!({ "ok": true, "override": updated, "lineup": document })))
}

/// Delete one override and store the roster without it.
async fn delete_lineup_override(
    State(state): State<AppState>,
    Path(override_id): Path<Uuid>,
) -> Result<StatusCode, ApiError> {
    let (channel_id, slots, overrides) = find_override(&state, override_id).await?;
    let overrides = overrides
        .into_iter()
        .filter(|o| o.id != override_id)
        .collect::<Vec<_>>();
    let document = json!({ "slots": slots, "overrides": overrides });
    state.store.lineup_set(channel_id, &document).await?;
    Ok(StatusCode::NO_CONTENT)
}

/// Preview the roster effective on a date. Body: `{ "date": "YYYY-MM-DD" }`.
async fn preview_lineup(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
    Json(body): Json<serde_json::Value>,
) -> Result<Json<serde_json::Value>, ApiError> {
    state.store.get(id).await.map_err(|_| api_not_found(id))?;
    let lineup = stored_lineup(&state, id).await?;
    let date = parse_date(body.get("date").and_then(serde_json::Value::as_str))
        .unwrap_or_else(|| chrono::Utc::now().date_naive());
    let (slots, matched) = crate::lineup::resolve_for_date(&lineup, date);
    Ok(Json(json!({
        "date": date.format("%Y-%m-%d").to_string(),
        "slots": slots,
        "override": matched.map(|o| json!({
            "id": o.id,
            "name": o.name,
            "kind": o.kind,
        })),
    })))
}

/// The stored roster, or a default when none was saved yet.
async fn stored_lineup(
    state: &AppState,
    channel_id: Uuid,
) -> Result<crate::lineup::ChannelLineup, StoreError> {
    match state.store.lineup_get(channel_id).await? {
        Some(value) => Ok(serde_json::from_value(value).unwrap_or_default()),
        None => Ok(crate::lineup::ChannelLineup::default()),
    }
}

/// Find the channel + roster holding an override by id.
async fn find_override(
    state: &AppState,
    override_id: Uuid,
) -> Result<
    (Uuid, Vec<crate::lineup::LineupSlot>, Vec<crate::lineup::LineupOverride>),
    ApiError,
> {
    for channel in state.store.list().await? {
        if let Some(value) = state.store.lineup_get(channel.id).await? {
            if let Ok(lineup) = serde_json::from_value::<crate::lineup::ChannelLineup>(value) {
                if lineup.overrides.iter().any(|o| o.id == override_id) {
                    return Ok((channel.id, lineup.slots, lineup.overrides));
                }
            }
        }
    }
    Err(api_not_found(override_id))
}

fn parse_override(body: &serde_json::Value) -> Result<crate::lineup::LineupOverride, ApiError> {
    let override_ = serde_json::from_value::<crate::lineup::LineupOverride>(body.clone())
        .map_err(|error| api_bad_request(format!("override is not valid: {error}")))?;
    match override_.kind.as_str() {
        "day_of_week" if matches!(override_.day_of_week, Some(d) if (0..=6).contains(&d)) => {}
        "specific_date" if validate_date(override_.specific_date.as_deref()) => {}
        _ => {
            return Err(api_bad_request(
                "an override needs kind day_of_week with day_of_week 0-6, or kind specific_date with a YYYY-MM-DD date"
                    .to_string(),
            ));
        }
    }
    Ok(override_)
}

fn validate_date(value: Option<&str>) -> bool {
    value.map(|v| parse_date(Some(v)).is_some()).unwrap_or(false)
}

fn parse_date(value: Option<&str>) -> Option<chrono::NaiveDate> {
    value.and_then(|v| chrono::NaiveDate::parse_from_str(v.trim(), "%Y-%m-%d").ok())
}

/// A 400 the editor can show verbatim.
fn api_bad_request(message: String) -> ApiError {
    ApiError(StoreError::Plugin(message))
}

/// A 404 for a missing channel (or override).
fn api_not_found(id: Uuid) -> ApiError {
    ApiError(StoreError::NotFound(id))
}

/// The Installed tab: the plugins in the registry, each joined with what the
/// manager knows about it (name, category, permissions, health).
async fn list_plugins(
    State(state): State<AppState>,
) -> Result<Json<serde_json::Value>, ApiError> {
    Ok(Json(installed_view(&state).await?))
}

/// Update an installed plugin to the newest version a repository offers.
/// Compiled-in plugins update with ChannelFlow itself; a staged plugin is
/// re-downloaded at the newer version.
async fn update_plugin(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let bundled = {
        let manager = state.plugins.lock().await;
        manager
            .catalog()
            .into_iter()
            .find(|entry| entry["id"] == json!(id))
            .and_then(|entry| entry["version"].as_str().map(str::to_string))
    };
    let registry = state.store.plugin_registry().await?;
    let current = registry
        .get(&id)
        .map(|entry| entry.version.clone())
        .or(bundled.clone());
    let Some(current) = current else {
        return Err(StoreError::PluginNotFound(id).into());
    };
    let Some(latest) = newest_version(&state, &id).await? else {
        return Ok(Json(json!({ "message": format!("no repository offers {id}") })));
    };
    if !version_is_newer(&latest, &current) {
        return Ok(Json(json!({ "message": "already up to date" })));
    }
    if bundled.is_some() {
        return Err(StoreError::Plugin(format!(
            "{id} is compiled into ChannelFlow, so it updates with ChannelFlow itself: pull the newer dependency from the ChannelFlow-Plugins repo and rebuild (latest is {latest})"
        ))
        .into());
    }
    let url = repository_for(&state, &id).await?;
    let rid = repo::current_rid();
    repo::install(
        &state.http,
        &state.store,
        &url,
        &id,
        Some(&latest),
        &rid,
        env!("CARGO_PKG_VERSION"),
    )
    .await?;
    Ok(Json(installed_view(&state).await?))
}

/// The newest version any registered repository offers for a plugin.
async fn newest_version(state: &AppState, id: &str) -> Result<Option<String>, ApiError> {
    let mut newest: Option<String> = None;
    for url in repository_urls(state).await? {
        let Ok(entries) = repo::fetch_catalog(&state.http, &url).await else {
            continue;
        };
        if let Some(entry) = entries.iter().find(|entry| entry.id == id) {
            for version in &entry.versions {
                if newest
                    .as_deref()
                    .map_or(true, |current| version_is_newer(&version.version, current))
                {
                    newest = Some(version.version.clone());
                }
            }
        }
    }
    Ok(newest)
}

/// The repository a plugin comes from, for a reinstall.
async fn repository_for(state: &AppState, id: &str) -> Result<String, ApiError> {
    for url in repository_urls(state).await? {
        if let Ok(entries) = repo::fetch_catalog(&state.http, &url).await {
            if entries.iter().any(|entry| entry.id == id) {
                return Ok(url);
            }
        }
    }
    Err(StoreError::Plugin(format!("no repository offers {id}")).into())
}

async fn repository_urls(state: &AppState) -> Result<Vec<String>, ApiError> {
    Ok(state
        .store
        .repo_list()
        .await?
        .iter()
        .filter_map(|repo| repo["url"].as_str().map(str::to_string))
        .collect())
}

#[derive(Deserialize)]
struct RemoveQuery {
    #[serde(default)]
    drop_database: bool,
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

/// The installed plugins: everything in the registry (the compiled-in set,
/// enabled or not) plus any staged install a repository delivered that is not
/// in the registry, tagged so the UI can tell them apart.
async fn installed_view(state: &AppState) -> Result<serde_json::Value, ApiError> {
    let registry = state.store.plugin_registry().await?;
    let staged = state.store.installed_list().await?;
    let catalog = {
        let manager = state.plugins.lock().await;
        manager.catalog()
    };
    let installed_ids: std::collections::HashSet<String> = registry
        .installed
        .iter()
        .map(|installed| installed.id.clone())
        .collect();
    // The plugins this one declares it needs, via its catalog entry (bundled
    // plugins) or an empty list.
    let deps_of = |catalog_entry: Option<&serde_json::Value>| -> Vec<String> {
        catalog_entry
            .and_then(|entry| entry["dependencies"].as_array())
            .map(|list| {
                list.iter()
                    .filter_map(|item| item.as_str().map(str::to_string))
                    .collect()
            })
            .unwrap_or_default()
    };
    let missing_of = |deps: &[String]| -> Vec<String> {
        deps.iter()
            .filter(|id| !installed_ids.contains(*id))
            .cloned()
            .collect()
    };
    // The newest version every registered repository offers, so an installed
    // plugin can show an Update action when a newer one is listed.
    let newest = newest_by_id(state).await;
    let mut plugins: Vec<serde_json::Value> = registry
        .installed
        .iter()
        .map(|installed| {
            let known = catalog.iter().find(|entry| entry["id"] == json!(installed.id));
            // A bundled plugin's real version is what this build compiled —
            // the catalog entry's version — not the stamp recorded at install
            // time. That way a rebuild that pulls a newer plugin shows the
            // newer version and no phantom update.
            let version = known
                .and_then(|entry| entry["version"].as_str())
                .unwrap_or(&installed.version)
                .to_string();
            let has_update = newest
                .get(&installed.id)
                .is_some_and(|latest| version_is_newer(latest, &version));
            let dependencies = deps_of(known);
            let missing_dependencies = missing_of(&dependencies);
            match known {
                Some(entry) => json!({
                    "id": installed.id,
                    "version": version,
                    "enabled": installed.enabled,
                    "bundled": true,
                    "staged": false,
                    "name": entry["name"],
                    "category": entry["category"],
                    "description": entry["description"],
                    "health": entry["health"],
                    "permissions": entry["permissions"],
                    "dependencies": dependencies,
                    "missing_dependencies": missing_dependencies,
                    "ui_contributions": entry["ui_contributions"],
                    "update_available": has_update,
                    "update_to": newest.get(&installed.id),
                }),
                None => json!({
                    "id": installed.id,
                    "version": version,
                    "enabled": installed.enabled,
                    "bundled": false,
                    "staged": false,
                    "name": installed.id,
                    "category": "",
                    "description": "This plugin is not part of this build.",
                    "health": serde_json::Value::Null,
                    "permissions": [],
                    "dependencies": dependencies,
                    "missing_dependencies": missing_dependencies,
                    "ui_contributions": [],
                    "update_available": has_update,
                    "update_to": newest.get(&installed.id),
                }),
            }
        })
        .collect();

    // Staged downloads a repository delivered and the registry does not know
    // about yet — they run once the dynamic loader arrives.
    for record in &staged {
        let Some(id) = record["id"].as_str() else { continue };
        if registry.get(id).is_some() {
            continue;
        }
        let version = record["version"].as_str().unwrap_or_default();
        let has_update = newest
            .get(id)
            .is_some_and(|latest| version_is_newer(latest, version));
        plugins.push(json!({
            "id": id,
            "version": record["version"],
            "enabled": false,
            "bundled": false,
            "staged": true,
            "name": id,
            "category": record["category"].as_str().unwrap_or(""),
            "description": "Downloaded and staged; it starts once dynamic loading lands.",
            "health": serde_json::Value::Null,
            "permissions": [],
            "ui_contributions": [],
            "update_available": has_update,
            "update_to": newest.get(id),
        }));
    }

    Ok(json!({ "plugins": plugins }))
}

/// The newest version every registered repository offers, keyed by plugin id.
/// Fetching the catalogs once here keeps a listing to one request per repo
/// instead of one per plugin.
async fn newest_by_id(state: &AppState) -> std::collections::HashMap<String, String> {
    let mut newest: std::collections::HashMap<String, String> = std::collections::HashMap::new();
    let Ok(urls) = repository_urls(state).await else {
        return newest;
    };
    for url in urls {
        let Ok(entries) = repo::fetch_catalog(&state.http, &url).await else {
            continue;
        };
        for entry in entries {
            let mut latest: Option<String> = None;
            for version in &entry.versions {
                if latest
                    .as_deref()
                    .map_or(true, |current| version_is_newer(&version.version, current))
                {
                    latest = Some(version.version.clone());
                }
            }
            if let Some(latest) = latest {
                let replace = newest
                    .get(&entry.id)
                    .map_or(true, |current| version_is_newer(&latest, current));
                if replace {
                    newest.insert(entry.id, latest);
                }
            }
        }
    }
    newest
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
    // A store listing should show the permissions the plugin asks for. The
    // repositories' manifest.json carries no permission list, so the value
    // comes from the plugin's own bundled manifest when this build has it
    // compiled in (empty = not part of this build).
    let known = {
        let manager = state.plugins.lock().await;
        manager.catalog()
    };
    let known: std::collections::HashMap<&str, &serde_json::Value> = known
        .iter()
        .filter_map(|entry| entry["id"].as_str().map(|id| (id, entry)))
        .collect();
    for plugin in &mut plugins {
        if let Some(entry) = plugin["id"].as_str().and_then(|id| known.get(id)) {
            plugin["permissions"] = entry["permissions"].clone();
        } else {
            plugin["permissions"] = serde_json::json!([]);
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

/// Install a plugin. A plugin that is part of this build is enabled in place
/// (nothing to download); one that is not is fetched from the repository,
/// verified, and staged — the dynamic loader runs it once that lands.
async fn install_plugin(
    State(state): State<AppState>,
    Json(input): Json<InstallRequest>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let bundled = {
        let manager = state.plugins.lock().await;
        manager
            .catalog()
            .into_iter()
            .find(|entry| entry["id"] == json!(input.id))
            .and_then(|entry| entry["version"].as_str().map(str::to_string))
    };

    if let Some(version) = bundled {
        let mut registry = state.store.plugin_registry().await?;
        registry.install(&input.id, &version);
        state.store.save_plugin_registry(&registry).await?;
        {
            let mut manager = state.plugins.lock().await;
            manager
                .enable(&input.id)
                .await
                .map_err(|error| StoreError::Plugin(error.to_string()))?;
        }
        return Ok(Json(installed_view(&state).await?));
    }

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

/// Remove a plugin: take it out of the registry and disable it if it is part
/// of this build, drop any staged install, and — when `?drop_database=true` —
/// erase the plugin's tables and key/value storage too.
async fn uninstall_plugin(
    State(state): State<AppState>,
    Path(id): Path<String>,
    Query(query): Query<RemoveQuery>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let mut removed_registry = false;
    {
        let mut registry = state.store.plugin_registry().await?;
        if registry.get(&id).is_some() {
            registry.remove(&id);
            state.store.save_plugin_registry(&registry).await?;
            removed_registry = true;
            let mut manager = state.plugins.lock().await;
            let _ = manager.disable(&id).await;
        }
    }
    let staged = state
        .store
        .installed_list()
        .await?
        .iter()
        .any(|record| record["id"].as_str() == Some(id.as_str()));
    if staged {
        let _ = repo::uninstall(&state.store, &id).await;
    }
    if !removed_registry && !staged {
        return Err(StoreError::PluginNotFound(id).into());
    }
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

// ── media sources and their connections ──────────────────────────────────

/// The media sources whose plugin is installed: identity, connection fields,
/// and supported media, for the connection pickers. A source whose plugin was
/// uninstalled stays out, so the tab only ever shows what is installed.
async fn list_media_sources(
    State(state): State<AppState>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let registry = state.store.plugin_registry().await?;
    let installed = |id: &str| registry.get(id).is_some();
    Ok(Json(json!({ "sources": state.media.catalog(&installed) })))
}

async fn list_connections(
    State(state): State<AppState>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let connections = state.store.connection_list().await?;
    Ok(Json(json!({ "connections": connections })))
}

#[derive(Deserialize)]
struct NewConnection {
    /// The media source's `type_id`, e.g. `jellyfin`.
    kind: String,
    /// The source's own connection object.
    config: serde_json::Value,
}

async fn create_connection(
    State(state): State<AppState>,
    Json(input): Json<NewConnection>,
) -> Result<(StatusCode, Json<serde_json::Value>), ApiError> {
    let registry = state.store.plugin_registry().await?;
    let installed = |id: &str| registry.get(id).is_some();
    if state.media.find_installed(&input.kind, &installed).is_none() {
        return Err(StoreError::Plugin(format!(
            "no media source {:?} is installed",
            input.kind
        ))
        .into());
    }
    let connection = state.store.connection_create(&input.kind, &input.config).await?;
    Ok((StatusCode::CREATED, Json(json!({ "connection": connection }))))
}

#[derive(Deserialize)]
struct UpdateConnection {
    config: serde_json::Value,
}

async fn update_connection(
    State(state): State<AppState>,
    Path(id): Path<i64>,
    Json(input): Json<UpdateConnection>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let connection = state
        .store
        .connection_update(id, &input.config)
        .await?
        .ok_or_else(|| StoreError::Plugin(format!("no connection with id {id}")))?;
    Ok(Json(json!({ "connection": connection })))
}

async fn delete_connection(
    State(state): State<AppState>,
    Path(id): Path<i64>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let removed = state
        .store
        .connection_delete(id)
        .await?
        .ok_or_else(|| StoreError::Plugin(format!("no connection with id {id}")))?;
    // The connection's own catalog rows (the base Media page) go with it.
    state.store.media_clear_connection(id).await?;
    // A media source's own rows cascade with the connection row; the sweep
    // removes the poster files of any item that then lost every source.
    if removed["kind"].as_str() == Some("jellyfin") {
        let db = state.store.plugin_database("com.channelflow.jellyfin").await;
        tokio::spawn(async move {
            channelflow_plugin_jellyfin::sweep_orphan_posters(db).await;
        });
    }
    Ok(Json(json!({ "removed": removed })))
}

/// Ask the connection's media-source plugin to test it: the server is
/// reachable, the key is accepted, or what went wrong — shaped for the
/// connections screen.
async fn test_connection(
    State(state): State<AppState>,
    Path(id): Path<i64>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let registry = state.store.plugin_registry().await?;
    let installed = |plugin_id: &str| registry.get(plugin_id).is_some();
    let connections = state.store.connection_list().await?;
    let row = connections
        .iter()
        .find(|connection| connection["id"].as_i64() == Some(id))
        .ok_or_else(|| StoreError::Plugin(format!("no connection with id {id}")))?;
    let kind = row["kind"].as_str().unwrap_or_default().to_string();
    let config: channelflow_plugin_api::media::Connection =
        serde_json::from_value(row["config"].clone()).map_err(|error| {
            StoreError::Plugin(format!("connection {id} has an invalid config: {error}"))
        })?;
    let source = state.media.find_installed(&kind, &installed).ok_or_else(|| {
        StoreError::Plugin(format!("no media source {:?} is installed", kind))
    })?;
    let result = source.test_connection(&config, &config.api_key).await;
    // Capture the server's identity so deep links (the Media page's play
    // button) can point at an item on this server.
    if result.ok {
        if let Some(meta) = source.server_info(&config, &config.api_key).await {
            let mut updated = config;
            updated.server_id = meta.get("server_id").and_then(serde_json::Value::as_str).map(str::to_string);
            updated.server_name = meta.get("server_name").and_then(serde_json::Value::as_str).map(str::to_string);
            let value = serde_json::to_value(&updated)
                .unwrap_or_else(|_| serde_json::json!(&updated));
            let _ = state.store.connection_update(id, &value).await;
        }
    }
    Ok(Json(json!({ "id": id, "kind": kind, "result": result })))
}

// ── tasks ──────────────────────────────────────────────────────────────────

/// The Jellyfin library-scan task's configuration and recent runs.
async fn jellyfin_sync_get(
    State(state): State<AppState>,
) -> Result<Json<serde_json::Value>, ApiError> {
    Ok(Json(json!({
        "config": tasks::load(&state.store).await,
        "runs": tasks::runs(&state.store).await,
    })))
}

async fn jellyfin_sync_put(
    State(state): State<AppState>,
    Json(config): Json<tasks::SyncConfig>,
) -> Result<Json<serde_json::Value>, ApiError> {
    if tasks::cron_expression(&config.schedule).is_none() {
        let message = if config.schedule.mode == "cron" {
            "enter a crontab expression like 0 3 * * *"
        } else {
            "enter a daily time like 03:00"
        };
        return Err(StoreError::Plugin(message.to_string()).into());
    }
    tasks::save(&state.store, &config).await?;
    Ok(Json(json!({
        "config": config,
        "runs": tasks::runs(&state.store).await,
    })))
}

#[derive(Deserialize)]
struct TaskRunBody {
    #[serde(default)]
    connection_id: Option<i64>,
}

/// Run the library scan now. With a `connection_id` it scans that one
/// connection (a library toggled on in the Library page) and is tagged
/// accordingly.
async fn jellyfin_sync_run(
    State(state): State<AppState>,
    Json(body): Json<TaskRunBody>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let trigger = if body.connection_id.is_some() {
        "library"
    } else {
        "manual"
    };
    let config_dir = std::path::PathBuf::from(&state.about.config_folder);
    let store = state.store.clone();
    let media = state.media.clone();
    let only_connection = body.connection_id;
    let trigger = trigger.to_string();
    // Run the scan in its own task: a big library takes a while, and it must
    // keep going even if the browser that started it navigates away (the
    // request future is dropped on disconnect).
    let handle = tokio::spawn(async move {
        tasks::run_sync(&store, &media, &config_dir, &trigger, only_connection).await
    });
    let run = handle
        .await
        .map_err(|error| StoreError::Plugin(format!("the scan task ended unexpectedly: {error}")))??;
    Ok(Json(json!({
        "run": run,
        "runs": tasks::runs(&state.store).await,
    })))
}

/// The Past Tense News scan task's configuration and recent runs.
async fn news_scan_get(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    Ok(Json(json!({
        "config": tasks::news_load(&state.store).await,
        "runs": tasks::news_runs(&state.store).await,
    })))
}

async fn news_scan_put(
    State(state): State<AppState>,
    Json(config): Json<tasks::NewsScanConfig>,
) -> Result<Json<serde_json::Value>, ApiError> {
    if tasks::news_cron_expression(&config.daily_time).is_none() {
        return Err(StoreError::Plugin("enter a daily time like 03:00".to_string()).into());
    }
    tasks::news_save(&state.store, &config).await?;
    Ok(Json(json!({
        "config": config,
        "runs": tasks::news_runs(&state.store).await,
    })))
}

/// Scan the Past Tense News folders into the media database now. With a
/// `connection_id` only that connection's folders are scanned (a Sync pressed
/// on the Library page), tagged `library`; without one every news connection
/// is scanned, tagged `manual`.
async fn news_scan_run(
    State(state): State<AppState>,
    Json(body): Json<TaskRunBody>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let trigger = if body.connection_id.is_some() {
        "library"
    } else {
        "manual"
    }
    .to_string();
    let config_dir = std::path::PathBuf::from(&state.about.config_folder);
    let store = state.store.clone();
    let media = state.media.clone();
    let only_connection = body.connection_id;
    // In its own task so it keeps going if the browser navigates away.
    let handle = tokio::spawn(async move {
        tasks::run_news_scan(&store, &media, &config_dir, &trigger, only_connection).await
    });
    let run = handle
        .await
        .map_err(|error| StoreError::Plugin(format!("the scan task ended unexpectedly: {error}")))??;
    Ok(Json(json!({
        "run": run,
        "runs": tasks::news_runs(&state.store).await,
    })))
}

/// The Local Files scan task's configuration and recent runs.
async fn local_scan_get(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    Ok(Json(json!({
        "config": tasks::local_load(&state.store).await,
        "runs": tasks::local_runs(&state.store).await,
    })))
}

async fn local_scan_put(
    State(state): State<AppState>,
    Json(config): Json<tasks::LocalScanConfig>,
) -> Result<Json<serde_json::Value>, ApiError> {
    if tasks::local_cron_expression(&config.daily_time).is_none() {
        return Err(StoreError::Plugin("enter a daily time like 03:00".to_string()).into());
    }
    tasks::local_save(&state.store, &config).await?;
    Ok(Json(json!({
        "config": config,
        "runs": tasks::local_runs(&state.store).await,
    })))
}

/// Scan the Local Files folders into the media database now. With a
/// `connection_id` only that connection's folders are scanned (a Sync pressed
/// on the Library page), tagged `library`; without one every local connection
/// is scanned, tagged `manual`.
async fn local_scan_run(
    State(state): State<AppState>,
    Json(body): Json<TaskRunBody>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let trigger = if body.connection_id.is_some() {
        "library"
    } else {
        "manual"
    }
    .to_string();
    let config_dir = std::path::PathBuf::from(&state.about.config_folder);
    let store = state.store.clone();
    let media = state.media.clone();
    let only_connection = body.connection_id;
    // In its own task so it keeps going if the browser navigates away.
    let handle = tokio::spawn(async move {
        tasks::run_local_scan(&store, &media, &config_dir, &trigger, only_connection).await
    });
    let run = handle
        .await
        .map_err(|error| StoreError::Plugin(format!("the scan task ended unexpectedly: {error}")))??;
    Ok(Json(json!({
        "run": run,
        "runs": tasks::local_runs(&state.store).await,
    })))
}

/// Whether a background task is running right now. The web UI asks this on
/// (re)load so a task that outlived its tab (or the whole page) brings its
/// progress popup back; nothing running means no popup.
async fn tasks_running() -> Json<serde_json::Value> {
    Json(json!({ "task": tasks::running() }))
}

// ── local media catalog ────────────────────────────────────────────────────

#[derive(Deserialize)]
struct MediaCatalogQuery {
    /// Restrict the reply to one kind: movie, series, album, artist, musicvideo, news.
    #[serde(default)]
    kind: Option<String>,
    /// Restrict the reply to a comma-separated set of kinds (preferred over `kind`).
    #[serde(default)]
    kinds: Option<String>,
    /// Page start (default 0).
    #[serde(default)]
    offset: Option<usize>,
    /// Page size. Absent/0 means "all rows" (detail-page lookups use this).
    #[serde(default)]
    limit: Option<usize>,
    /// Substring filter on the title.
    #[serde(default)]
    q: Option<String>,
}

/// The image route's single parameter: the poster's own path.
#[derive(Deserialize)]
struct MediaImageQuery {
    path: String,
}

/// The Media page's data: counts per tab plus the rows that match `kind` (or
/// everything when no kind is given). Rows come from the core's own catalog,
/// so the page works the same for every media source.
async fn media_catalog_list(
    State(state): State<AppState>,
    Query(query): Query<MediaCatalogQuery>,
) -> Result<Json<serde_json::Value>, ApiError> {
    // Tab counts always cover the whole catalog; the items are one page so the
    // Media page lazy-loads instead of pulling everything at once.
    let counts = {
        let pairs = state.store.media_counts().await?;
        let mut counts = serde_json::Map::new();
        for tab in ["movies", "tvshows", "music", "musicvideos", "news"] {
            counts.insert(tab.to_string(), serde_json::json!(0));
        }
        for (kind, count) in pairs {
            let tab = match kind.as_str() {
                "movie" => "movies",
                "series" => "tvshows",
                "album" | "artist" => "music",
                "musicvideo" => "musicvideos",
                "news" => "news",
                _ => continue,
            };
            if let Some(entry) = counts.get_mut(tab) {
                *entry = serde_json::json!(count);
            }
        }
        counts
    };
    let kinds: Vec<String> = query
        .kinds
        .as_deref()
        .map(|kinds| {
            kinds
                .split(',')
                .map(str::trim)
                .filter(|kind| {
                    matches!(*kind, "movie" | "series" | "album" | "artist" | "musicvideo" | "news")
                })
                .map(str::to_string)
                .collect()
        })
        .or_else(|| query.kind.as_deref().filter(|kind| !kind.is_empty()).map(|kind| vec![kind.to_string()]))
        .unwrap_or_default();
    let offset = query.offset.unwrap_or(0);
    let limit = query.limit.filter(|value| *value > 0);
    let (rows, total) = state
        .store
        .media_page(&kinds, offset, limit, query.q.as_deref().unwrap_or(""))
        .await?;
    let mut rows = rows;
    enrich_source_web_urls(&state, &mut rows).await?;
    Ok(Json(json!({
        "counts": counts,
        "items": rows,
        "total": total,
        "offset": offset,
        "limit": limit,
    })))
}

/// One item in the base catalog, by match key — the Media detail page's source
/// of truth. Sources carry `web_url` deep links into the media servers.
// ── Music graph ──────────────────────────────────────────────────────────
// Derived from the base catalog itself — artists, albums and music videos are
// catalog rows; an album's tracks are `track` rows whose detail.album_id
// matches the album's source remote id. No plugin tables involved.

/// The normalized form media.js uses to match music names: lowercase, drop
/// everything but letters/digits, and a leading a/an/the.
fn normalize_music_name(name: &str) -> String {
    let lowered: String = name.to_lowercase();
    let compact: String = lowered
        .chars()
        .filter(|c| c.is_alphanumeric())
        .collect();
    for article in ["the", "an", "a"] {
        if let Some(rest) = compact.strip_prefix(article) {
            return rest.to_string();
        }
    }
    compact
}

/// A name → artist match_key map from the catalog's artist rows.
fn artist_index(rows: &[serde_json::Value]) -> std::collections::HashMap<String, String> {
    let mut index = std::collections::HashMap::new();
    for row in rows {
        if row["kind"].as_str() != Some("artist") {
            continue;
        }
        if let (Some(name), Some(key)) = (row["title"].as_str(), row["match_key"].as_str()) {
            index.insert(normalize_music_name(name), key.to_string());
        }
    }
    index
}

fn artist_name_of(row: &serde_json::Value) -> Option<String> {
    if let Some(name) = row["detail"]["album_artist"].as_str() {
        if !name.is_empty() {
            return Some(name.to_string());
        }
    }
    row["detail"]["artists"]
        .as_array()
        .and_then(|artists| artists.first())
        .and_then(serde_json::Value::as_str)
        .map(str::to_string)
}

async fn music_artists(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    let rows = state.store.media_list().await?;
    let artists: Vec<serde_json::Value> = rows
        .iter()
        .filter(|row| row["kind"].as_str() == Some("artist"))
        .map(|row| json!({ "id": row["match_key"], "name": row["title"] }))
        .collect();
    Ok(Json(json!({ "artists": artists })))
}

async fn music_albums(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    let rows = state.store.media_list().await?;
    let index = artist_index(&rows);
    let albums: Vec<serde_json::Value> = rows
        .iter()
        .filter(|row| row["kind"].as_str() == Some("album"))
        .map(|row| {
            let artist_name = artist_name_of(row);
            let artist_id = artist_name
                .as_deref()
                .map(normalize_music_name)
                .and_then(|name| index.get(&name).cloned());
            json!({
                "id": row["match_key"],
                "title": row["title"],
                "year": row["year"],
                "artist_id": artist_id,
                "artist_name": artist_name,
            })
        })
        .collect();
    Ok(Json(json!({ "albums": albums })))
}

async fn music_videos(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    let rows = state.store.media_list().await?;
    let index = artist_index(&rows);
    let videos: Vec<serde_json::Value> = rows
        .iter()
        .filter(|row| row["kind"].as_str() == Some("musicvideo"))
        .map(|row| {
            let artist_name = artist_name_of(row);
            let artist_id = artist_name
                .as_deref()
                .map(normalize_music_name)
                .and_then(|name| index.get(&name).cloned());
            json!({
                "id": row["match_key"],
                "title": row["title"],
                "year": row["year"],
                "poster_path": row["poster_path"],
                "artist_id": artist_id,
                "artist_name": artist_name,
            })
        })
        .collect();
    Ok(Json(json!({ "videos": videos })))
}

async fn music_album_tracks(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let rows = state.store.media_list().await?;
    let album = rows
        .iter()
        .find(|row| row["match_key"].as_str() == Some(id.as_str()));
    let Some(album) = album else {
        return Ok(Json(json!({ "tracks": [], "track_count": 0 })));
    };
    let remote_ids: std::collections::HashSet<String> = album["sources"]
        .as_array()
        .map(|sources| {
            sources
                .iter()
                .filter_map(|source| source["remote_id"].as_str().map(str::to_string))
                .collect()
        })
        .unwrap_or_default();
    let mut tracks: Vec<serde_json::Value> = rows
        .iter()
        .filter(|row| row["kind"].as_str() == Some("track"))
        .filter(|row| {
            row["detail"]["album_id"]
                .as_str()
                .map(|album_id| remote_ids.contains(album_id))
                .unwrap_or(false)
        })
        .map(|row| {
            json!({
                "title": row["title"],
                "runtime_ticks": row["runtime_seconds"].as_i64().map(|s| s * 10_000_000),
                "track_number": row["detail"]["index_number"],
                "disc_number": row["detail"]["parent_index_number"],
            })
        })
        .collect();
    tracks.sort_by(|a, b| {
        let disc = a["disc_number"].as_i64().unwrap_or(i64::MAX)
            .cmp(&b["disc_number"].as_i64().unwrap_or(i64::MAX));
        disc.then_with(|| {
            a["track_number"].as_i64().unwrap_or(i64::MAX)
                .cmp(&b["track_number"].as_i64().unwrap_or(i64::MAX))
        })
    });
    Ok(Json(json!({ "tracks": tracks, "track_count": tracks.len() })))
}

async fn media_catalog_item(
    State(state): State<AppState>,
    Path(match_key): Path<String>,
) -> Result<Response, ApiError> {
    let mut rows = state.store.media_list().await?;
    let mut item = rows
        .into_iter()
        .find(|row| row["match_key"].as_str() == Some(match_key.as_str()));
    match item {
        Some(mut item) => {
            enrich_source_web_urls(&state, std::slice::from_mut(&mut item)).await?;
            Ok(Json(json!({ "item": item })).into_response())
        }
        None => Ok((StatusCode::NOT_FOUND, Json(json!({ "error": "no such item" })))
            .into_response()),
    }
}

/// Attach a `web_url` to every source whose media-source plugin knows how to
/// deep-link into its server's web UI (the Media page's play buttons).
async fn enrich_source_web_urls(
    state: &AppState,
    items: &mut [serde_json::Value],
) -> Result<(), ApiError> {
    let registry = state.store.plugin_registry().await?;
    let installed = |plugin_id: &str| registry.get(plugin_id).is_some();
    let connections = state.store.connection_list().await?;
    let configs: std::collections::HashMap<i64, channelflow_plugin_api::media::Connection> =
        connections
            .into_iter()
            .filter_map(|row| {
                let id = row["id"].as_i64()?;
                let config: channelflow_plugin_api::media::Connection =
                    serde_json::from_value(row["config"].clone()).ok()?;
                Some((id, config))
            })
            .collect();
    for item in items.iter_mut() {
        let Some(sources) = item.get_mut("sources").and_then(serde_json::Value::as_array_mut)
        else {
            continue;
        };
        for source in sources.iter_mut() {
            let connection_id = source["connection_id"].as_i64().unwrap_or(0);
            let kind = source["source_kind"].as_str().unwrap_or("");
            let remote_id = source["remote_id"].as_str().unwrap_or("");
            let Some(config) = configs.get(&connection_id).cloned() else {
                continue;
            };
            let Some(media_source) = state.media.find_installed(kind, &installed) else {
                continue;
            };
            if let Some(web_url) = media_source.item_web_url(&config, remote_id) {
                source["web_url"] = serde_json::json!(web_url);
            }
        }
    }
    Ok(())
}

// ── TV: seasons and episodes, live from the media server ─────────────────
// Seasons aren't stored by the sync (the TV walk can't finish on a slow
// server), so the Media page asks the server directly; the base holds the
// connection secret and the browser never sees it.

#[derive(Deserialize)]
struct TvSeasonsQuery {
    match_key: String,
}

#[derive(Deserialize)]
struct TvEpisodesQuery {
    match_key: String,
    season_id: String,
}

#[derive(Deserialize)]
struct TvEpisodeImageQuery {
    connection_id: i64,
    item_id: String,
}

fn tv_connection_config(
    connections: &[serde_json::Value],
    connection_id: i64,
) -> Option<channelflow_plugin_api::media::Connection> {
    connections
        .iter()
        .find(|row| row["id"].as_i64() == Some(connection_id))
        .and_then(|row| serde_json::from_value(row["config"].clone()).ok())
}

/// The jellyfin source (connection id + remote id) behind a catalog item.
fn jellyfin_source_of(item: &serde_json::Value) -> Option<(i64, String)> {
    item["sources"].as_array().and_then(|sources| {
        sources.iter().find_map(|source| {
            if source["source_kind"].as_str() == Some("jellyfin") {
                Some((
                    source["connection_id"].as_i64().unwrap_or(0),
                    source["remote_id"].as_str().unwrap_or("").to_string(),
                ))
            } else {
                None
            }
        })
    })
}

/// The authenticated GET helpers for a connection (header + query forms the
/// Jellyfin client uses), with a bounded timeout so a slow server never hangs
/// the Media page.
struct TvJellyfin<'a> {
    client: &'a reqwest::Client,
    connection: channelflow_plugin_api::media::Connection,
}

impl<'a> TvJellyfin<'a> {
    fn new(
        client: &'a reqwest::Client,
        connections: &[serde_json::Value],
        connection_id: i64,
    ) -> Option<Self> {
        tv_connection_config(connections, connection_id).map(|connection| Self {
            client,
            connection,
        })
    }

    fn get(&self, path: &str) -> reqwest::RequestBuilder {
        let base = self.connection.url.trim().trim_end_matches('/');
        let key = &self.connection.api_key;
        self.client
            .get(format!("{base}{path}"))
            .header("X-Emby-Token", key)
            .header("Authorization", format!("MediaBrowser Token=\"{key}\""))
            .header("Accept", "application/json")
            .timeout(std::time::Duration::from_secs(25))
    }
}

/// The seasons of the series behind a match key, straight from the server.
async fn tv_seasons(State(state): State<AppState>, Query(query): Query<TvSeasonsQuery>) -> Response {
    let Ok(rows) = state.store.media_list().await else {
        return (StatusCode::INTERNAL_SERVER_ERROR, Json(json!({ "error": "catalog unavailable" }))).into_response();
    };
    let item = match rows.iter().find(|row| row["match_key"].as_str() == Some(query.match_key.as_str())) {
        Some(item) => item,
        None => return (StatusCode::NOT_FOUND, Json(json!({ "error": "no such series" }))).into_response(),
    };
    let Ok(connections) = state.store.connection_list().await else {
        return (StatusCode::INTERNAL_SERVER_ERROR, Json(json!({ "error": "connections unavailable" }))).into_response();
    };
    let Some((connection_id, series_id)) = jellyfin_source_of(item) else {
        return Json(json!({ "seasons": [] })).into_response();
    };
    let Some(tv) = TvJellyfin::new(&state.http, &connections, connection_id) else {
        return Json(json!({ "seasons": [] })).into_response();
    };
    let response = match tv.get(&format!("/Shows/{series_id}/Seasons")).send().await {
        Ok(response) => response,
        Err(_) => return Json(json!({ "seasons": [] })).into_response(),
    };
    if !response.status().is_success() {
        return Json(json!({ "seasons": [] })).into_response();
    }
    let value: serde_json::Value = match response.json().await {
        Ok(value) => value,
        Err(_) => return Json(json!({ "seasons": [] })).into_response(),
    };
    let seasons: Vec<serde_json::Value> = value
        .get("Items")
        .and_then(serde_json::Value::as_array)
        .cloned()
        .unwrap_or_default()
        .into_iter()
        .filter_map(|season| {
            let id = season.get("Id").and_then(serde_json::Value::as_str)?.to_string();
            let name = season.get("Name").and_then(serde_json::Value::as_str).map(str::to_string);
            let number = season.get("IndexNumber").and_then(serde_json::Value::as_i64);
            Some(serde_json::json!({ "id": id, "name": name, "number": number }))
        })
        .collect();
    Json(json!({
        "title": item["title"],
        "match_key": item["match_key"],
        "seasons": seasons,
    }))
    .into_response()
}

/// The episodes of one season, straight from the server.
async fn tv_episodes(State(state): State<AppState>, Query(query): Query<TvEpisodesQuery>) -> Response {
    let Ok(rows) = state.store.media_list().await else {
        return (StatusCode::INTERNAL_SERVER_ERROR, Json(json!({ "error": "catalog unavailable" }))).into_response();
    };
    let item = match rows.iter().find(|row| row["match_key"].as_str() == Some(query.match_key.as_str())) {
        Some(item) => item,
        None => return (StatusCode::NOT_FOUND, Json(json!({ "error": "no such series" }))).into_response(),
    };
    let Ok(connections) = state.store.connection_list().await else {
        return (StatusCode::INTERNAL_SERVER_ERROR, Json(json!({ "error": "connections unavailable" }))).into_response();
    };
    let Some((connection_id, series_id)) = jellyfin_source_of(item) else {
        return Json(json!({ "episodes": [] })).into_response();
    };
    let Some(connection) = tv_connection_config(&connections, connection_id) else {
        return Json(json!({ "episodes": [] })).into_response();
    };
    let tv = TvJellyfin::new(&state.http, &connections, connection_id).expect("connection present");
    let fields = "Name,IndexNumber,ParentIndexNumber,ProductionYear,RunTimeTicks,Overview,ImageTags,ProviderIds";
    let response = match tv
        .get(&format!("/Shows/{series_id}/Episodes?SeasonId={}&Fields={fields}", query.season_id))
        .send()
        .await
    {
        Ok(response) => response,
        Err(_) => return Json(json!({ "episodes": [] })).into_response(),
    };
    if !response.status().is_success() {
        return Json(json!({ "episodes": [] })).into_response();
    }
    let value: serde_json::Value = match response.json().await {
        Ok(value) => value,
        Err(_) => return Json(json!({ "episodes": [] })).into_response(),
    };
    let registry = match state.store.plugin_registry().await {
        Ok(registry) => registry,
        Err(_) => return Json(json!({ "episodes": [] })).into_response(),
    };
    let installed = |plugin_id: &str| registry.get(plugin_id).is_some();
    let series_web = state
        .media
        .find_installed("jellyfin", &installed)
        .and_then(|source| source.item_web_url(&connection, &series_id))
        .unwrap_or_default();
    let episodes: Vec<serde_json::Value> = value
        .get("Items")
        .and_then(serde_json::Value::as_array)
        .cloned()
        .unwrap_or_default()
        .into_iter()
        .filter_map(|episode| {
            let id = episode.get("Id").and_then(serde_json::Value::as_str)?.to_string();
            let title = episode.get("Name").and_then(serde_json::Value::as_str).map(str::to_string);
            let number = episode.get("IndexNumber").and_then(serde_json::Value::as_i64);
            let season = episode.get("ParentIndexNumber").and_then(serde_json::Value::as_i64);
            let runtime = episode.get("RunTimeTicks").and_then(serde_json::Value::as_i64);
            let overview = episode.get("Overview").and_then(serde_json::Value::as_str).map(str::to_string);
            let year = episode.get("ProductionYear").and_then(serde_json::Value::as_i64);
            let has_image = episode
                .get("ImageTags")
                .and_then(|tags| tags.get("Primary"))
                .is_some();
            let web_url = if series_web.is_empty() {
                String::new()
            } else {
                series_web.replace(&series_id, &id)
            };
            Some(serde_json::json!({
                "id": id,
                "title": title,
                "number": number,
                "season": season,
                "runtime_ticks": runtime,
                "overview": overview,
                "year": year,
                "has_image": has_image,
                "web_url": web_url,
            }))
        })
        .collect();
    Json(json!({
        "title": item["title"],
        "match_key": item["match_key"],
        "connection_id": connection_id,
        "season_id": query.season_id,
        "episodes": episodes,
    }))
    .into_response()
}

/// Proxy one episode's image (a frame of the video) from the server.
async fn tv_episode_image(
    State(state): State<AppState>,
    Query(query): Query<TvEpisodeImageQuery>,
) -> Response {
    let Ok(connections) = state.store.connection_list().await else {
        return StatusCode::INTERNAL_SERVER_ERROR.into_response();
    };
    let Some(tv) = TvJellyfin::new(&state.http, &connections, query.connection_id) else {
        return StatusCode::NOT_FOUND.into_response();
    };
    let response = match tv
        .get(&format!("/Items/{}/Images/Primary", query.item_id))
        .send()
        .await
    {
        Ok(response) => response,
        Err(_) => return StatusCode::NOT_FOUND.into_response(),
    };
    if !response.status().is_success() {
        return StatusCode::NOT_FOUND.into_response();
    }
    let content_type = response
        .headers()
        .get(reqwest::header::CONTENT_TYPE)
        .and_then(|value| value.to_str().ok())
        .unwrap_or("image/jpeg")
        .to_string();
    let bytes = match response.bytes().await {
        Ok(bytes) => bytes,
        Err(_) => return StatusCode::NOT_FOUND.into_response(),
    };
    (
        [
            (header::CONTENT_TYPE, content_type),
            (header::CACHE_CONTROL, "no-cache".to_string()),
        ],
        axum::body::Bytes::from(bytes),
    )
        .into_response()
}

/// A map from a source's remote id to the catalog item it belongs to. Plugin
/// pages (a person's filmography, say) use it to link their own rows into the
/// Media catalog's item pages and to borrow the catalog's poster. Keyed
/// `"<source_kind>:<connection_id>:<remote_id>"`; each value is
/// `{ match_key, poster_path, title, year, kind }`.
async fn media_source_index(
    State(state): State<AppState>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let rows = state.store.media_list().await?;
    let mut index = serde_json::Map::new();
    for item in &rows {
        let Some(match_key) = item["match_key"].as_str() else {
            continue;
        };
        let Some(sources) = item["sources"].as_array() else {
            continue;
        };
        for source in sources {
            let kind = source["source_kind"].as_str().unwrap_or("");
            let connection_id = source["connection_id"].as_i64().unwrap_or(0);
            let remote_id = source["remote_id"].as_str().unwrap_or("");
            if kind.is_empty() || remote_id.is_empty() {
                continue;
            }
            index.insert(
                format!("{kind}:{connection_id}:{remote_id}"),
                serde_json::json!({
                    "match_key": match_key,
                    "poster_path": item["poster_path"],
                    "title": item["title"],
                    "year": item["year"],
                    "kind": item["kind"],
                }),
            );
        }
    }
    Ok(Json(json!({ "index": index })))
}

/// Serve one poster from `<config>/Images`. The path is the absolute path a
/// sync wrote; only files under the images root are served.
async fn media_catalog_image(
    State(state): State<AppState>,
    Query(query): Query<MediaImageQuery>,
) -> Response {
    let path = query.path;
    let root = state.store.images_dir();
    let candidate = std::path::PathBuf::from(&path);
    // Only files under the images root may be served. Normalize with
    // canonicalize() so `..`/symlink tricks cannot escape the boundary.
    let Ok(root_canonical) = root.canonicalize() else {
        return (StatusCode::NOT_FOUND, "images store is not ready").into_response();
    };
    // Two spellings reach here: paths relative to the images root (the form
    // plugins now record, e.g. `posters/Movies/x.jpg`) and older
    // working-dir-relative paths (`./config/Images/posters/Movies/x.jpg`).
    // Try the images-root spelling first, then the working-directory one.
    let spellings: Vec<std::path::PathBuf> = if candidate.is_absolute() {
        vec![candidate]
    } else {
        vec![root.join(&candidate), candidate]
    };
    let mut canonical = None;
    for spelling in &spellings {
        if let Ok(resolved) = spelling.canonicalize() {
            if resolved.starts_with(&root_canonical) && resolved.is_file() {
                canonical = Some(resolved);
                break;
            }
        }
    }
    let Some(canonical) = canonical else {
        return StatusCode::NOT_FOUND.into_response();
    };
    let bytes = match std::fs::read(&canonical) {
        Ok(bytes) => bytes,
        Err(_) => return StatusCode::NOT_FOUND.into_response(),
    };
    let content_type = canonical
        .extension()
        .and_then(|extension| extension.to_str())
        .map(|extension| match extension.to_ascii_lowercase().as_str() {
            "png" => "image/png",
            "webp" => "image/webp",
            "gif" => "image/gif",
            "svg" => "image/svg+xml",
            _ => "image/jpeg",
        })
        .unwrap_or("image/jpeg");
    ([(header::CONTENT_TYPE, content_type)], bytes).into_response()
}

/// Write next's `lineup.json` and every enabled channel's config, pointing at
/// the scheduler's playout output.
async fn regenerate_playout(
    State(state): State<AppState>,
) -> Result<Json<serde_json::Value>, ApiError> {
    let config_root = state.store.config_dir();
    let channels = state.store.list().await?;
    let playout_base = scheduler_playout_base(&state, &config_root).await;
    match crate::playout::write(&config_root, &channels, &playout_base) {
        Ok(paths) => Ok(Json(
            json!({ "lineup": paths.lineup, "channels": paths.channels }),
        )),
        Err(error) => Err(StoreError::Plugin(error).into()),
    }
}

/// The folder the scheduler writes playout JSON under: its `playout_dir`
/// setting, else `<config>/playout`.
async fn scheduler_playout_base(
    state: &AppState,
    config_root: &std::path::Path,
) -> std::path::PathBuf {
    let configured = state
        .store
        .plugin_get("com.channelflow.scheduler", "settings")
        .await
        .ok()
        .flatten()
        .and_then(|value| {
            value
                .get("playout_dir")
                .and_then(serde_json::Value::as_str)
                .map(str::to_string)
        })
        .unwrap_or_default();
    if configured.trim().is_empty() {
        config_root.join("playout")
    } else {
        std::path::PathBuf::from(configured.trim())
    }
}

/// The streaming paths the Live TV page advertises.
///
/// Only port `8097` is published and the encoder listens inside the container
/// on another port, so every URL the page shows is written against ChannelFlow's
/// own origin. These paths require an API key (the server key or a paired
/// client's), presented as `?apiKey=` or `X-Api-Key`. With the encoder running
/// they proxy next's HLS through this origin (the playlist's segment URLs are
/// rewritten); without it they answer `503` with a sentence rather than `404`,
/// so a player gets an honest "not yet" instead of "no such thing".
async fn live_asset(
    State(state): State<AppState>,
    headers: HeaderMap,
    Query(query): Query<ApiKeyQuery>,
    Path(asset): Path<String>,
) -> Response {
    if let Err(response) = live_guard(&state, &headers, &query).await {
        return response;
    }
    if !state.encoder.running() {
        return live_pending_message();
    }
    let client = state.encoder.client();
    let base = format!("http://127.0.0.1:{}", state.encoder.port());
    if asset == "channels.m3u" {
        return proxy_bytes(client, &format!("{base}/channels.m3u"), false).await;
    }
    if asset == "xmltv.xml" {
        return proxy_bytes(client, &format!("{base}/xmltv.xml"), false).await;
    }
    if let Some(number) = asset.strip_suffix(".m3u8") {
        return proxy_bytes(client, &format!("{base}/channel/{number}.m3u8"), true).await;
    }
    (
        StatusCode::NOT_FOUND,
        [(
            axum::http::header::CONTENT_TYPE,
            "text/plain; charset=utf-8",
        )],
        "no such live path\n",
    )
        .into_response()
}

/// One HLS segment, streamed through from the encoder.
async fn live_segment(
    State(state): State<AppState>,
    headers: HeaderMap,
    Query(query): Query<ApiKeyQuery>,
    Path((channel, file)): Path<(String, String)>,
) -> Response {
    if let Err(response) = live_guard(&state, &headers, &query).await {
        return response;
    }
    if !state.encoder.running() {
        return live_pending_message();
    }
    let client = state.encoder.client();
    let url = format!(
        "http://127.0.0.1:{}/session/{channel}/{file}",
        state.encoder.port()
    );
    proxy_bytes(client, &url, false).await
}

/// Check the API key, returning the `401` response to send when it fails.
async fn live_guard(
    state: &AppState,
    headers: &HeaderMap,
    query: &ApiKeyQuery,
) -> Result<(), Response> {
    let provided = headers
        .get("x-api-key")
        .and_then(|value| value.to_str().ok())
        .map(str::to_string)
        .or(query.apikey.clone())
        .unwrap_or_default();
    if state.store.api_key_valid(&provided).await.unwrap_or(false) {
        Ok(())
    } else {
        Err((
            StatusCode::UNAUTHORIZED,
            [(
                axum::http::header::CONTENT_TYPE,
                "text/plain; charset=utf-8",
            )],
            "A valid API key is required (add ?apiKey=… to the URL).\n",
        )
            .into_response())
    }
}

/// The `503` a live path answers while nothing is encoding.
fn live_pending_message() -> Response {
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

/// Fetch `url` from the encoder and return it; `rewrite` fixes an HLS
/// playlist's segment URLs to ChannelFlow's own origin.
async fn proxy_bytes(client: &reqwest::Client, url: &str, rewrite: bool) -> Response {
    match client.get(url).send().await {
        Ok(upstream) if upstream.status().is_success() => {
            let content_type = upstream
                .headers()
                .get(axum::http::header::CONTENT_TYPE)
                .cloned();
            let bytes = match upstream.bytes().await {
                Ok(bytes) => bytes,
                Err(error) => {
                    return (
                        StatusCode::BAD_GATEWAY,
                        format!("encoder read failed: {error}\n"),
                    )
                        .into_response()
                }
            };
            let body = if rewrite {
                crate::encoder::rewrite_playlist(&String::from_utf8_lossy(&bytes)).into_bytes()
            } else {
                bytes.to_vec()
            };
            let mut response = Response::builder().status(StatusCode::OK);
            if let Some(content_type) = content_type {
                response = response.header(axum::http::header::CONTENT_TYPE, content_type);
            }
            response
                .body(axum::body::Body::from(body))
                .unwrap_or_else(|_| StatusCode::INTERNAL_SERVER_ERROR.into_response())
        }
        Ok(upstream) => (
            StatusCode::BAD_GATEWAY,
            format!("encoder answered {}\n", upstream.status()),
        )
            .into_response(),
        Err(error) => (
            StatusCode::BAD_GATEWAY,
            format!("encoder unreachable: {error}\n"),
        )
            .into_response(),
    }
}

#[derive(Deserialize)]
struct ApiKeyQuery {
    #[serde(rename = "apiKey")]
    apikey: Option<String>,
}

// The web UI is served from <config>/webui — plain files this binary loads,
// not embedded code. The document itself is no-store: it must never sit in a
// browser cache, or an operator's edit leaves users staring at a stale page.
//
// When the UI files are missing, `/` answers with a short page saying where
// to put them instead of a blank screen; assets 404.
const WEBUI_MISSING: &str = r#"<!doctype html><meta charset="utf-8"><title>ChannelFlow — web UI not installed</title>
<body style="font-family:sans-serif;background:#101010;color:#eee;padding:2rem">
<h1>ChannelFlow</h1>
<p>The web UI is not installed. This binary only loads it — it reads the UI
files from <code>&lt;config&gt;/webui</code>.</p>
<p>Copy the UI into the config directory, for example:</p>
<pre>scripts/install-webui.sh ./config</pre>
<p>or by hand:</p>
<pre>cp crates/channelflow-core/static/* ./config/webui/</pre>
</body>"#;

async fn index(State(state): State<AppState>) -> Response {
    let body = match crate::webui::read(&state.store.config_dir(), "index.html") {
        Ok(body) => body,
        Err(_) => {
            tracing::warn!(
                root = %state.store.config_dir().display(),
                "web UI missing under <config>/webui"
            );
            return (
                [
                    (axum::http::header::CONTENT_TYPE, "text/html; charset=utf-8"),
                    (axum::http::header::CACHE_CONTROL, "no-store"),
                ],
                WEBUI_MISSING,
            )
                .into_response();
        }
    };
    (
        [
            (axum::http::header::CONTENT_TYPE, "text/html; charset=utf-8"),
            (axum::http::header::CACHE_CONTROL, "no-store"),
        ],
        axum::body::Bytes::from(body),
    )
        .into_response()
}

async fn css(State(state): State<AppState>) -> Response {
    webui_file(&state, "text/css; charset=utf-8", "app.css")
}

async fn js(State(state): State<AppState>) -> Response {
    webui_file(&state, "text/javascript; charset=utf-8", "app.js")
}

/// Serve one per-page asset (`pages/<name>.<ext>`) from `<config>/webui`. The
/// wildcard path is confined to the `pages/` directory, so a page can ship its
/// own JS, CSS, HTML or images without exposing the rest of the config tree.
async fn page_asset(State(state): State<AppState>, Path(path): Path<String>) -> Response {
    let content_type = match path.rsplit('.').next() {
        Some("css") => "text/css; charset=utf-8",
        Some("js") => "text/javascript; charset=utf-8",
        Some("html") => "text/html; charset=utf-8",
        Some("svg") => "image/svg+xml",
        Some("png") => "image/png",
        Some("jpg") | Some("jpeg") => "image/jpeg",
        Some("webp") => "image/webp",
        Some("gif") => "image/gif",
        _ => "application/octet-stream",
    };
    match crate::webui::read_nested(&state.store.config_dir(), &format!("pages/{path}")) {
        Ok(body) => (
            [
                (header::CONTENT_TYPE, content_type),
                (header::CACHE_CONTROL, "no-cache"),
            ],
            axum::body::Bytes::from(body),
        )
            .into_response(),
        Err(_) => StatusCode::NOT_FOUND.into_response(),
    }
}

/// Serve a web file a plugin publishes from its own folder. The plugin handed
/// the file to its `PluginWeb` during `on_load`; the manager serves it here at
/// `/plugin/{id}/web/{path}`. Only the exact files the plugin published are
/// reachable, so `..`-style traversal has nothing to climb.
async fn plugin_web_asset(
    State(state): State<AppState>,
    Path((id, path)): Path<(String, String)>,
) -> Response {
    let path = sanitize_plugin_web_path(&path);
    let asset = match path {
        Some(path) => state.plugins.lock().await.web_asset(&id, &path),
        None => None,
    };
    match asset {
        Some(asset) => (
            [
                (header::CONTENT_TYPE, asset.mime.as_str()),
                (header::CACHE_CONTROL, "no-cache"),
            ],
            axum::body::Bytes::from(asset.bytes),
        )
            .into_response(),
        None => StatusCode::NOT_FOUND.into_response(),
    }
}

/// Rebuild a plugin web path from its normal components only, so `.`-parent
/// components and Windows prefixes are dropped rather than followed.
fn sanitize_plugin_web_path(raw: &str) -> Option<String> {
    use std::path::Component;
    let mut safe = std::path::PathBuf::new();
    for part in std::path::Path::new(raw).components() {
        match part {
            Component::Normal(segment) => safe.push(segment),
            _ => return None,
        }
    }
    let safe = safe.to_string_lossy().replace('\\', "/");
    if safe.starts_with('/') || safe.contains("../") {
        return None;
    }
    Some(safe)
}

/// Serve one media-source logo from `<config>/webui/logos` (the badge images
/// the Media page shows instead of source-name pills). Only image extensions
/// and plain filenames are accepted.
async fn logo_badge(State(state): State<AppState>, Path(name): Path<String>) -> Response {
    let content_type = match name.rsplit('.').next() {
        Some("png") => "image/png",
        Some("jpg") | Some("jpeg") => "image/jpeg",
        Some("webp") => "image/webp",
        Some("svg") => "image/svg+xml",
        _ => return StatusCode::NOT_FOUND.into_response(),
    };
    match crate::webui::read_nested(&state.store.config_dir(), &format!("logos/{name}")) {
        Ok(body) => (
            [
                (header::CONTENT_TYPE, content_type),
                (header::CACHE_CONTROL, "no-cache"),
            ],
            axum::body::Bytes::from(body),
        )
            .into_response(),
        Err(_) => StatusCode::NOT_FOUND.into_response(),
    }
}

/// Serve one web-ui asset from `<config>/webui`. The bytes may change when the
/// operator edits the file, and carry no ETag or Last-Modified, so they are
/// always revalidated rather than heuristically cached.
fn webui_file(
    state: &AppState,
    content_type: &'static str,
    name: &'static str,
) -> Response {
    match crate::webui::read(&state.store.config_dir(), name) {
        Ok(body) => (
            [
                (header::CONTENT_TYPE, content_type),
                (header::CACHE_CONTROL, "no-cache"),
            ],
            axum::body::Bytes::from(body),
        )
            .into_response(),
        Err(_) => StatusCode::NOT_FOUND.into_response(),
    }
}

async fn logo(State(state): State<AppState>) -> Response {
    webui_file(&state, "image/png", "logo.png")
}

async fn favicon(State(state): State<AppState>) -> Response {
    webui_file(&state, "image/x-icon", "favicon.ico")
}

async fn favicon_32(State(state): State<AppState>) -> Response {
    webui_file(&state, "image/png", "favicon-32x32.png")
}

async fn favicon_16(State(state): State<AppState>) -> Response {
    webui_file(&state, "image/png", "favicon-16x16.png")
}

async fn apple_touch_icon(State(state): State<AppState>) -> Response {
    webui_file(&state, "image/png", "apple-touch-icon.png")
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
            web: channelflow_plugin_api::PluginWeb::new(),
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
        let _ = router(store, about, plugins, Arc::new(crate::media::MediaSources::new()), http, Arc::new(crate::encoder::Encoder::new(std::path::PathBuf::from("/nonexistent/lineup.json"), 8409)), routers);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
