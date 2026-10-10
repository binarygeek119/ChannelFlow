//! HTTP surface: the JSON API for channels and plugins plus the static web UI
//! shell. Plugins contribute their own routers, nested under `/api/plugins`.

use std::sync::Arc;

use axum::{
    extract::{Path, Query, Request, State},
    http::{header, HeaderMap, HeaderValue, StatusCode, Uri},
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::{delete, get, post, put},
    Json, Router,
};
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
}

pub fn router(
    store: Store,
    about_info: AboutInfo,
    plugins: Arc<Mutex<PluginManager>>,
    media: Arc<MediaSources>,
    http: reqwest::Client,
    plugin_routers: Vec<(String, Router)>,
) -> Router {
    let state = AppState {
        store,
        about: about_info,
        plugins,
        media,
        http,
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
        .route("/api/auth/state", get(auth_state))
        .route("/api/auth/login", post(login))
        .route("/api/auth/logout", post(logout))
        .route("/api/auth/setup", post(setup))
        .route("/api/auth/forgot", post(forgot))
        .route("/api/auth/reset", post(reset_password))
        .route("/api/setup/database", post(setup_database))
        .route("/api/channels", get(list_channels).post(create_channel))
        .route(
            "/api/channels/{id}",
            get(get_channel).put(update_channel).delete(delete_channel),
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
        .route("/api/tasks/running", get(tasks_running))
        .route("/api/media", get(media_catalog_list))
        .route("/api/media/source-index", get(media_source_index))
        .route("/api/media/{match_key}", get(media_catalog_item))
        .route("/api/media/image", get(media_catalog_image))
        .route("/live/{asset}", get(live_pending))
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

/// The session idles out after six hours of no activity (the window slides on
/// every request — see `store::session_valid`). Remember me decides whether
/// the browser keeps the cookie past the current browser session, so a
/// remembered login survives a restart and stays until it idles out.
const SESSION_TTL_HOURS: i64 = crate::store::SESSION_TTL_HOURS;
/// How long a remembered cookie lives in the browser. It only has to outlast
/// the idle window; the server is what actually enforces the timeout.
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
    state.store.save_session(&token, expires_at).await?;
    let cookie = if input.remember {
        format!(
            "channelflow_session={token}; Path=/; HttpOnly; SameSite=Lax; Max-Age={REMEMBER_COOKIE_SECS}"
        )
    } else {
        format!("channelflow_session={token}; Path=/; HttpOnly; SameSite=Lax")
    };
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
    let urls = crate::quickpin::payload(&settings, true);
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
/// ciphertext to the relay. The relay forwards it to the waiting app; `404`
/// from the relay means the PIN was unknown, expired, or already used.
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
    let payload = crate::quickpin::payload(&settings, primary_local);
    let plaintext = serde_json::to_vec(&payload).map_err(StoreError::Json)?;
    let ciphertext = crate::quickpin::encrypt(&input.pin, &plaintext)
        .map_err(|error| StoreError::Plugin(format!("could not encrypt the pairing details: {error}")))?;
    let delivered = crate::quickpin::deliver(&state.http, &server, &input.pin, &ciphertext)
        .await
        .map_err(StoreError::Plugin)?;
    Ok(Json(json!({ "delivered": delivered, "server": server })))
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

/// Whether a background task is running right now. The web UI asks this on
/// (re)load so a task that outlived its tab (or the whole page) brings its
/// progress popup back; nothing running means no popup.
async fn tasks_running() -> Json<serde_json::Value> {
    Json(json!({ "task": tasks::running() }))
}

// ── local media catalog ────────────────────────────────────────────────────

#[derive(Deserialize)]
struct MediaCatalogQuery {
    /// Restrict the reply to one kind: movie, series, album, artist, musicvideo.
    #[serde(default)]
    kind: Option<String>,
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
    let mut rows = state.store.media_list().await?;
    enrich_source_web_urls(&state, &mut rows).await?;
    let counts = {
        let mut counts = serde_json::Map::new();
        for tab in ["movies", "tvshows", "music", "musicvideos"] {
            counts.insert(tab.to_string(), serde_json::json!(0));
        }
        for row in &rows {
            let kind = row["kind"].as_str().unwrap_or("");
            let tab = match kind {
                "movie" => "movies",
                "series" => "tvshows",
                "album" | "artist" => "music",
                "musicvideo" => "musicvideos",
                _ => continue,
            };
            if let Some(count) = counts.get_mut(tab) {
                *count = serde_json::Value::Number(serde_json::Number::from(
                    count.as_i64().unwrap_or(0) + 1,
                ));
            }
        }
        counts
    };
    let items = match query.kind.as_deref() {
        None => rows,
        Some(kind) => rows
            .into_iter()
            .filter(|row| row["kind"].as_str() == Some(kind))
            .collect(),
    };
    Ok(Json(json!({ "counts": counts, "items": items })))
}

/// One item in the base catalog, by match key — the Media detail page's source
/// of truth. Sources carry `web_url` deep links into the media servers.
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

/// A map from a source's remote id to the catalog `match_key` of the item it
/// belongs to. Plugin pages (a person's filmography, say) use it to link their
/// own rows into the Media catalog's item pages. Keyed
/// `"<source_kind>:<connection_id>:<remote_id>"`.
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
                serde_json::json!(match_key),
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
        let _ = router(store, about, plugins, Arc::new(crate::media::MediaSources::new()), http, routers);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
