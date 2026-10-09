mod api;
mod auth;
mod media;
mod model;
mod plugin;
mod store;
mod webui;

use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context, Result};
use channelflow_plugin_api::plugin::{PluginApi, PluginLogger};
use clap::Parser;
use tokio::sync::Mutex;
use tracing_subscriber::EnvFilter;

use crate::plugin::PluginManager;
use crate::store::Store;

#[derive(Parser, Debug)]
#[command(
    name = "channelflow",
    version,
    about = "ChannelFlow 2.0.0 — live TV channels built on ErsatzTV next"
)]
struct Args {
    /// Config directory holding channels and, later, playout state.
    #[arg(long, env = "CHANNELFLOW_CONFIG")]
    config: Option<PathBuf>,

    /// Postgres connection string, e.g. postgres://user:pass@host:5432/db.
    /// Empty (or unset) stores everything as JSON files under `--config`.
    #[arg(long, env = "DATABASE_URL", default_value = "")]
    database_url: String,

    /// Address to bind.
    #[arg(long, default_value = "0.0.0.0")]
    bind: String,

    /// Port to listen on.
    #[arg(long, env = "PORT", default_value_t = 8097)]
    port: u16,
}

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info")),
        )
        .init();

    let args = Args::parse();
    let started = std::time::Instant::now();
    let config = args.config.unwrap_or_else(|| PathBuf::from("config"));

    // The image runs as `ersatztv` (uid 1000, inherited from the next base), so
    // a bind-mounted config directory owned by someone else fails here. Say so
    // plainly instead of surfacing a bare EACCES.
    std::fs::create_dir_all(&config).map_err(|error| {
        if error.kind() == std::io::ErrorKind::PermissionDenied {
            anyhow::anyhow!(
                "permission denied creating {}: the config directory must be writable by uid 1000 (`ersatztv`)",
                config.display()
            )
        } else {
            anyhow::Error::new(error)
                .context(format!("creating config directory {}", config.display()))
        }
    })?;

    // The web UI lives as editable files under <config>/webui; write the
    // defaults on first boot and after that they are served from disk.
    webui::ensure(&config).with_context(|| {
        format!(
            "writing the web UI under {} — that directory must be writable",
            config.join("webui").display()
        )
    })?;

    let store = if args.database_url.trim().is_empty() {
        let store = Store::open(&config).with_context(|| {
            format!(
                "opening the channel store under {} — that directory must be writable by uid 1000 (`ersatztv`)",
                config.display()
            )
        })?;
        // The setup walkthrough can configure a Postgres connection; honor it
        // the same way `--database-url` would.
        if let Some(url) = store.database_url().await? {
            if !url.trim().is_empty() {
                store
                    .connect_database(&url)
                    .await
                    .with_context(|| format!("connecting to the configured Postgres at {}", redact_url(&url)))?;
            }
        }
        store
    } else {
        Store::open_postgres(&args.database_url, &config)
            .await
            .with_context(|| {
                format!(
                    "connecting to Postgres at {} — the database must exist and this app must be able to create tables in it",
                    redact_url(&args.database_url)
                )
            })?
    };
    // The one-time moves of pre-plugin settings into their plugins' own
    // storage happen before those plugins load and read them.
    store
        .upgrade_legacy_ai()
        .await
        .context("moving legacy AI settings into plugin storage")?;
    store
        .upgrade_legacy_transcode()
        .await
        .context("moving legacy transcode settings into plugin storage")?;
    store.seed().await?;

    // ── plugins ─────────────────────────────────────────────────────────────
    let http = reqwest::Client::builder()
        .timeout(Duration::from_secs(30))
        .build()
        .context("building the outbound HTTP client for plugins")?;

    let mut manager = PluginManager::new(env!("CARGO_PKG_VERSION"));

    let ai_plugin = channelflow_plugin_ai::plugin();
    let ai_manifest = ai_plugin.metadata().clone();
    let ai_api = PluginApi {
        id: ai_manifest.id.clone(),
        storage: store.plugin_storage(&ai_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&ai_manifest.id),
        logger: PluginLogger::new(&ai_manifest.id),
        core: Arc::new(store.core_data()),
        database: store.plugin_database(&ai_manifest.id).await,
    };
    manager
        .add(ai_plugin, ai_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the AI plugin: {error}"))?;

    // The ErsatzTV transcoding plugin reads the channel list through the
    // api:core:read handle, so it gets the same store-backed CoreData.
    let ersatztv_plugin = channelflow_plugin_ersatztv::plugin();
    let ersatztv_manifest = ersatztv_plugin.metadata().clone();
    let ersatztv_api = PluginApi {
        id: ersatztv_manifest.id.clone(),
        storage: store.plugin_storage(&ersatztv_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&ersatztv_manifest.id),
        logger: PluginLogger::new(&ersatztv_manifest.id),
        core: Arc::new(store.core_data()),
        database: store.plugin_database(&ersatztv_manifest.id).await,
    };
    manager
        .add(ersatztv_plugin, ersatztv_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the ErsatzTV plugin: {error}"))?;

    // The Jellyfin media source syncs remote libraries into its own tables
    // and registers as a MediaSource for the connection forms and sync driver.
    let jellyfin_plugin = channelflow_plugin_jellyfin::plugin();
    let jellyfin_manifest = jellyfin_plugin.metadata().clone();
    let jellyfin_api = PluginApi {
        id: jellyfin_manifest.id.clone(),
        storage: store.plugin_storage(&jellyfin_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&jellyfin_manifest.id),
        logger: PluginLogger::new(&jellyfin_manifest.id),
        core: Arc::new(store.core_data()),
        database: store.plugin_database(&jellyfin_manifest.id).await,
    };
    manager
        .add(jellyfin_plugin, jellyfin_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the Jellyfin plugin: {error}"))?;

    // Plugins are loaded but not pre-installed: the Installed tab starts empty
    // and the Store offers every plugin. The registry is the source of truth
    // after that, so installing or removing a plugin is not undone by a
    // restart.
    let registry = store.plugin_registry().await?;
    for installed in &registry.installed {
        if !installed.enabled {
            continue;
        }
        if let Err(error) = manager.enable(&installed.id).await {
            tracing::warn!(plugin = %installed.id, %error, "could not enable installed plugin");
        }
    }

    let plugin_routers = manager.routers();
    let plugins = Arc::new(Mutex::new(manager));

    // The media sources: plugins that implement the MediaSource contract
    // register their connection forms and sync drives here.
    let mut media_sources = media::MediaSources::new();
    media_sources.register(
        "com.channelflow.jellyfin",
        channelflow_plugin_jellyfin::media_source(),
    );

    // The plugin store: seed the ChannelFlow-Plugins repository so the Store
    // tab has something to show, unless the operator points it elsewhere.
    if store.repo_list().await?.is_empty() {
        let url = std::env::var("CHANNELFLOW_PLUGIN_STORE").unwrap_or_else(|_| {
            "https://raw.githubusercontent.com/binarygeek119/ChannelFlow-Plugins/main/manifest.json"
                .to_string()
        });
        match store.repo_add(&url).await {
            Ok(_) => tracing::info!(repository = %url, "registered the default plugin repository"),
            Err(error) => tracing::warn!(repository = %url, %error, "could not register the default plugin repository"),
        }
    }

    // ── server ───────────────────────────────────────────────────────────────
    let addr: SocketAddr = format!("{}:{}", args.bind, args.port)
        .parse()
        .with_context(|| format!("invalid bind address {}:{}", args.bind, args.port))?;

    let channels = store.list().await?.len();
    let backend = if args.database_url.trim().is_empty() {
        "files"
    } else {
        "postgres"
    };
    tracing::info!(config = %config.display(), backend, channels, "ChannelFlow 2.0.0 started");
    tracing::info!("web UI on http://{addr}/");

    let listener = tokio::net::TcpListener::bind(addr).await?;
    let bound = listener.local_addr()?;
    let about = api::AboutInfo {
        config_folder: config.display().to_string(),
        listen_port: bound.port(),
        started,
    };
    axum::serve(
        listener,
        api::router(store, about, plugins, Arc::new(media_sources), http, plugin_routers),
    )
    .await?;
    Ok(())
}

/// Show a connection string without its password in logs and errors.
fn redact_url(url: &str) -> String {
    let host = url.split('@').last().unwrap_or(url);
    let shown: String = host.chars().take(120).collect();
    if url.contains('@') {
        format!("<credentials>@{shown}")
    } else {
        shown
    }
}