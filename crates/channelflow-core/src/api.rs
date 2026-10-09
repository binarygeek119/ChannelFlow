//! HTTP surface: the JSON API for channels and plugins plus the static web UI
//! shell. Plugins contribute their own routers, nested under `/api/plugins`.

use std::sync::Arc;

use axum::{
    extract::{Path, Query, Request, State},
    http::{header, HeaderValue, StatusCode},
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
        .route("/logo.png", get(logo))
        .route("/favicon.ico", get(favicon))
        .route("/favicon-32x32.png", get(favicon_32))
        .route("/favicon-16x16.png", get(favicon_16))
        .route("/apple-touch-icon.png", get(apple_touch_icon))
        .route("/api/about", get(about))
        .route("/api/health", get(health))
        .route("/api/auth/state", get(auth_state))
        .route("/api/auth/login", post(login))
        .route("/api/auth/logout", post(logout))
        .route("/api/auth/setup", post(setup))
        .route("/api/auth/forgot", post(forgot))
        .route("/api/auth/reset", post(reset_password))
        .route("/api/auth/reset-setup", post(reset_setup))
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
        .route("/live/{asset}", get(live_pending))
        .with_state(state.clone());
    for (id, plugin_router) in plugin_routers {
        app = app.nest(&format!("/api/plugins/{id}"), plugin_router);
    }
    app.layer(middleware::from_fn_with_state(state, require_auth))
        .layer(middleware::from_fn(no_store))
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

/// Gate every `/api/*` request behind the session once setup has completed.
/// Until then the walkthrough needs the API open; the auth endpoints and the
/// health check stay public either way, as do the static assets.
async fn require_auth(State(state): State<AppState>, request: Request, next: Next) -> Response {
    let path = request.uri().path();
    if !path.starts_with("/api/")
        || path.starts_with("/api/auth/")
        || path == "/api/health"
    {
        return next.run(request).await;
    }
    let setup_done = match state.store.auth_record().await {
        Ok(Some(record)) => record.setup_complete,
        _ => false,
    };
    if !setup_done {
        return next.run(request).await;
    }
    let token = request
        .headers()
        .get(header::COOKIE)
        .and_then(|value| value.to_str().ok())
        .and_then(|cookie| {
            cookie
                .split(';')
                .find_map(|part| {
                    part.trim()
                        .strip_prefix("channelflow_session=")
                        .map(str::to_string)
                })
        });
    let valid = match (token, state.store.session_token().await) {
        (Some(cookie), Ok(Some(stored))) => auth::verify_token(&cookie, &stored),
        _ => false,
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

/// Whether setup is done and whether this request is logged in. The shell
/// reads this first thing and shows the walkthrough, the login screen, or the
/// app accordingly.
async fn auth_state(
    State(state): State<AppState>,
    headers: axum::http::HeaderMap,
) -> Result<Json<serde_json::Value>, ApiError> {
    let record = state.store.auth_record().await?;
    let setup_done = record.as_ref().map(|r| r.setup_complete).unwrap_or(false);
    let authenticated = setup_done && {
        let cookie = headers
            .get(header::COOKIE)
            .and_then(|value| value.to_str().ok())
            .and_then(|cookie| {
                cookie
                    .split(';')
                    .find_map(|part| {
                        part.trim()
                            .strip_prefix("channelflow_session=")
                            .map(str::to_string)
                    })
            });
        match (cookie, state.store.session_token().await) {
            (Some(cookie), Ok(Some(stored))) => auth::verify_token(&cookie, &stored),
            _ => false,
        }
    };
    Ok(Json(json!({
        "setup_done": setup_done,
        "authenticated": authenticated,
        "username": record.map(|r| r.username),
    })))
}

#[derive(Deserialize)]
struct LoginBody {
    username: String,
    password: String,
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
    state.store.save_session(&token).await?;
    Ok((
        [(header::SET_COOKIE, format!("channelflow_session={token}; Path=/; HttpOnly; SameSite=Lax"))],
        Json(json!({ "ok": true })),
    )
        .into_response())
}

async fn logout(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    state.store.clear_session().await?;
    Ok(Json(json!({ "ok": true })))
}

/// Forget the account (and session and install registry) so the instance runs
/// the first-boot walkthrough again. Meant for the login screen's "first
/// time?" reset; plugin data and connections are kept.
async fn reset_setup(State(state): State<AppState>) -> Result<Json<serde_json::Value>, ApiError> {
    state.store.reset_setup().await?;
    state.store.clear_session().await?;
    tracing::info!("reset setup — instance will run first-boot again");
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
    state
        .store
        .connect_database(&url)
        .await
        .map_err(|error| StoreError::Plugin(format!("Postgres: {error}")))?;
    state.store.save_database_url(&url).await?;
    Ok(Json(json!({ "ok": true, "database": "postgres" })))
}

/// How long is a password-reset pin valid for, and the cooldown between
/// resets.
const RESET_COOLDOWN_SECONDS: i64 = 600;
const RESET_PIN_TTL_SECONDS: i64 = 1800;

/// Start a password reset: write a fresh random pin to a file in the config
/// directory (`reset-<MM-DD-YY-HH-MM-SS>.txt`) that only someone with filesystem
/// access can read. Nothing about the pin goes through the web UI.
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
    std::fs::write(
        &path,
        format!("ChannelFlow password reset pin\n\npin: {pin}\n"),
    )
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
            "plugins are compiled into ChannelFlow, so {id} updates with ChannelFlow, not on its own (latest is {latest})"
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
    let mut plugins: Vec<serde_json::Value> = registry
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
                    "staged": false,
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
                    "staged": false,
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

    // Staged downloads a repository delivered and the registry does not know
    // about yet — they run once the dynamic loader arrives.
    for record in &staged {
        let Some(id) = record["id"].as_str() else { continue };
        if registry.get(id).is_some() {
            continue;
        }
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
        }));
    }

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
// directory and cannot start with a half-copied web root. The document itself
// is served no-store: it must never sit in a browser cache, or an upgrade
// leaves users staring at a stale login screen while the server has moved on.
async fn index() -> Response {
    (
        [
            (axum::http::header::CONTENT_TYPE, "text/html; charset=utf-8"),
            (axum::http::header::CACHE_CONTROL, "no-store"),
        ],
        include_str!("../static/index.html"),
    )
        .into_response()
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
        let _ = router(store, about, plugins, Arc::new(crate::media::MediaSources::new()), http, routers);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
