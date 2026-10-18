mod api;
mod auth;
mod client_logs;
mod encoder;
mod lineup;
mod media;
mod model;
mod plugin;
mod playout;
mod quickpin;
mod settings;
mod store;
mod tasks;
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
use tracing_subscriber::layer::SubscriberExt;
use tracing_subscriber::util::SubscriberInitExt;

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
    let args = Args::parse();
    let config = args.config.clone().unwrap_or_else(|| PathBuf::from("config"));

    // Rolling log file under <config>/logs (i.e. /config/logs in the image),
    // rotated daily, in addition to stdout so Docker still captures output.
    let log_dir = config.join("logs");
    let _ = std::fs::create_dir_all(&log_dir);
    let file_appender = tracing_appender::rolling::daily(&log_dir, "channelflow.log");
    let (non_blocking, _log_guard) = tracing_appender::non_blocking(file_appender);

    let env_filter = EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info"));
    let stdout_layer = tracing_subscriber::fmt::layer();
    let file_layer = tracing_subscriber::fmt::layer()
        .with_writer(non_blocking)
        .with_ansi(false);
    tracing_subscriber::registry()
        .with(env_filter)
        .with(stdout_layer)
        .with(file_layer)
        .init();

    let started = std::time::Instant::now();

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

    // The binary is a loader: the web UI is plain files read from
    // <config>/webui on every request. Seed them from the shipped copy (the
    // image keeps it at /usr/share/channelflow/webui; CHANNELFLOW_WEBUI
    // overrides) when they are missing, and refresh them when the shipped copy
    // has changed — the UI rides along with the binary, so a new image must not
    // keep serving the old UI out of a persistent /config. A stamp of what was
    // last copied keeps an unchanged image a no-op.
    let shipped = std::env::var("CHANNELFLOW_WEBUI")
        .unwrap_or_else(|_| "/usr/share/channelflow/webui".to_string());
    let src = PathBuf::from(&shipped);
    let installed = webui::webui_dir(&config).join("index.html").exists();
    if src.join("index.html").exists() {
        let stamp = webui::stamp(&src);
        if webui::read_stamp(&config).as_deref() != Some(stamp.as_str()) {
            match webui::copy_from(&src, &config) {
                Ok(count) => {
                    let _ = webui::write_stamp(&config, &stamp);
                    tracing::info!(
                        root = %config.join("webui").display(),
                        copied = count,
                        refreshed = installed,
                        "copied the web UI from {shipped}"
                    );
                }
                Err(error) => tracing::warn!(%error, shipped, "could not copy the web UI"),
            }
        }
    } else if !installed {
        tracing::warn!(
            root = %config.join("webui").display(),
            "web UI files are missing and no CHANNELFLOW_WEBUI source was found — copy crates/channelflow-core/static/* into <config>/webui, or run scripts/install-webui.sh"
        );
    }

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
        tasks: tasks::plugin_host(&ai_manifest.id),
        storage: store.plugin_storage(&ai_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&ai_manifest.id),
        logger: PluginLogger::new(&ai_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&ai_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
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
        tasks: tasks::plugin_host(&ersatztv_manifest.id),
        storage: store.plugin_storage(&ersatztv_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&ersatztv_manifest.id),
        logger: PluginLogger::new(&ersatztv_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&ersatztv_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
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
        tasks: tasks::plugin_host(&jellyfin_manifest.id),
        storage: store.plugin_storage(&jellyfin_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&jellyfin_manifest.id),
        logger: PluginLogger::new(&jellyfin_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&jellyfin_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
    };
    manager
        .add(jellyfin_plugin, jellyfin_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the Jellyfin plugin: {error}"))?;

    // The Off Air plugin owns the settings for what plays on dead air; its
    // page contribution is what draws the Off Air tab in the nav.
    let offair_plugin = channelflow_plugin_offair::plugin();
    let offair_manifest = offair_plugin.metadata().clone();
    let offair_api = PluginApi {
        id: offair_manifest.id.clone(),
        tasks: tasks::plugin_host(&offair_manifest.id),
        storage: store.plugin_storage(&offair_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&offair_manifest.id),
        logger: PluginLogger::new(&offair_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&offair_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
    };
    manager
        .add(offair_plugin, offair_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the Off Air plugin: {error}"))?;

    // The CommercialBrainz plugin owns the ad-avail connection settings; its
    // page contribution draws the CommercialBrainz tab.
    let commercialbrainz_plugin = channelflow_plugin_commercialbrainz::plugin();
    let commercialbrainz_manifest = commercialbrainz_plugin.metadata().clone();
    let commercialbrainz_api = PluginApi {
        id: commercialbrainz_manifest.id.clone(),
        tasks: tasks::plugin_host(&commercialbrainz_manifest.id),
        storage: store.plugin_storage(&commercialbrainz_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&commercialbrainz_manifest.id),
        logger: PluginLogger::new(&commercialbrainz_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&commercialbrainz_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
    };
    manager
        .add(commercialbrainz_plugin, commercialbrainz_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the CommercialBrainz plugin: {error}"))?;

    // The Emergency Broadcast System plugin owns the weather-alert overlay
    // settings; its page contribution draws its tab.
    let emergency_plugin = channelflow_plugin_emergency::plugin();
    let emergency_manifest = emergency_plugin.metadata().clone();
    let emergency_api = PluginApi {
        id: emergency_manifest.id.clone(),
        tasks: tasks::plugin_host(&emergency_manifest.id),
        storage: store.plugin_storage(&emergency_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&emergency_manifest.id),
        logger: PluginLogger::new(&emergency_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&emergency_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
    };
    manager
        .add(emergency_plugin, emergency_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the Emergency plugin: {error}"))?;

    // The Weather plugin owns the WeatherStar settings; its page contribution
    // draws the Weather tab.
    let weather_plugin = channelflow_plugin_weather::plugin();
    let weather_manifest = weather_plugin.metadata().clone();
    let weather_api = PluginApi {
        id: weather_manifest.id.clone(),
        tasks: tasks::plugin_host(&weather_manifest.id),
        storage: store.plugin_storage(&weather_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&weather_manifest.id),
        logger: PluginLogger::new(&weather_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&weather_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
    };
    manager
        .add(weather_plugin, weather_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the Weather plugin: {error}"))?;

    // The News plugin owns the newscast settings; its page contribution
    // draws the News tab.
    let news_plugin = channelflow_plugin_news::plugin();
    let news_manifest = news_plugin.metadata().clone();
    let news_api = PluginApi {
        id: news_manifest.id.clone(),
        tasks: tasks::plugin_host(&news_manifest.id),
        storage: store.plugin_storage(&news_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&news_manifest.id),
        logger: PluginLogger::new(&news_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&news_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
    };
    manager
        .add(news_plugin, news_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the News plugin: {error}"))?;

    // The Presets plugin offers the ready-made Binarygeek119 lineup; its page
    // contribution draws the Presets tab. It only reads core channels.
    let presets_plugin = channelflow_plugin_presets::plugin();
    let presets_manifest = presets_plugin.metadata().clone();
    let presets_api = PluginApi {
        id: presets_manifest.id.clone(),
        tasks: tasks::plugin_host(&presets_manifest.id),
        storage: store.plugin_storage(&presets_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&presets_manifest.id),
        logger: PluginLogger::new(&presets_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&presets_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
    };
    manager
        .add(presets_plugin, presets_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the Presets plugin: {error}"))?;

    // The Lists plugin owns named collections of catalog items; its page
    // contribution draws the Lists tab.
    let lists_plugin = channelflow_plugin_lists::plugin();
    let lists_manifest = lists_plugin.metadata().clone();
    let lists_api = PluginApi {
        id: lists_manifest.id.clone(),
        tasks: tasks::plugin_host(&lists_manifest.id),
        storage: store.plugin_storage(&lists_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&lists_manifest.id),
        logger: PluginLogger::new(&lists_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&lists_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
    };
    manager
        .add(lists_plugin, lists_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the Lists plugin: {error}"))?;

    // The Holiday Channel plugin owns the seasonal calendar: which holiday's
    // window the date falls in, the period logos, and the matching lineup.
    let holiday_plugin = channelflow_plugin_holidaychannel::plugin();
    let holiday_manifest = holiday_plugin.metadata().clone();
    let holiday_api = PluginApi {
        id: holiday_manifest.id.clone(),
        tasks: tasks::plugin_host(&holiday_manifest.id),
        storage: store.plugin_storage(&holiday_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&holiday_manifest.id),
        logger: PluginLogger::new(&holiday_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&holiday_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
    };
    manager
        .add(holiday_plugin, holiday_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the Holiday Channel plugin: {error}"))?;

    // The Local plugin is a file-based media source (no server): its page
    // browses folders and creates connections; the sync scans folders for
    // .nfo metadata + poster.jpg/png.
    let local_plugin = channelflow_plugin_local::plugin();
    let local_manifest = local_plugin.metadata().clone();
    let local_api = PluginApi {
        id: local_manifest.id.clone(),
        tasks: tasks::plugin_host(&local_manifest.id),
        storage: store.plugin_storage(&local_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&local_manifest.id),
        logger: PluginLogger::new(&local_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&local_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
    };
    manager
        .add(local_plugin, local_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the Local plugin: {error}"))?;

    // The Past Tense News plugin scans a folder of news events into the media
    // database as "news" items (TV-show-style, no seasons).
    let pasttense_plugin = channelflow_plugin_pasttense::plugin();
    let pasttense_manifest = pasttense_plugin.metadata().clone();
    let pasttense_api = PluginApi {
        id: pasttense_manifest.id.clone(),
        tasks: tasks::plugin_host(&pasttense_manifest.id),
        storage: store.plugin_storage(&pasttense_manifest.id),
        http: http.clone(),
        base_version: env!("CARGO_PKG_VERSION").to_string(),
        dir: store.plugin_dir(&pasttense_manifest.id),
        logger: PluginLogger::new(&pasttense_manifest.id),
        core: Arc::new(store.core_data(http.clone())),
        database: store.plugin_database(&pasttense_manifest.id).await,
        web: channelflow_plugin_api::PluginWeb::new(),
    };
    manager
        .add(pasttense_plugin, pasttense_api)
        .await
        .map_err(|error| anyhow::anyhow!("loading the Past Tense News plugin: {error}"))?;

    // The Scheduler plugin is now a standalone, installable plugin (not
    // compiled in), so it can be updated via the Store without rebuilding
    // ChannelFlow. Install it from the Store tab if needed.
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
    media_sources.register("com.channelflow.local", channelflow_plugin_local::media_source());
    media_sources.register(
        "com.channelflow.pasttense",
        channelflow_plugin_pasttense::media_source(),
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
    let media_sources = Arc::new(media_sources);

    // The scheduler: every half minute, run the Jellyfin library scan when its
    // daily/cron schedule is due. It talks to the store and the plugins
    // directly, so it needs no browser.
    {
        let store = store.clone();
        let media = media_sources.clone();
        let config_dir = config.clone();
        tokio::spawn(async move {
            let mut ticker = tokio::time::interval(Duration::from_secs(30));
            ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
            loop {
                ticker.tick().await;
                let config = tasks::load(&store).await;
                if !tasks::scan_running()
                    && tasks::is_due(&store, &config, chrono::Utc::now()).await
                {
                    if let Err(error) =
                        tasks::run_sync(&store, &media, &config_dir, "schedule", None).await
                    {
                        tracing::warn!(%error, "scheduled Jellyfin scan failed");
                    }
                }
            }
        });
    }

    // And the Past Tense News scan on its own daily timer: it only touches the
    // news connections, so it never waits on a slow media-server walk.
    {
        let store = store.clone();
        let media = media_sources.clone();
        let config_dir = config.clone();
        tokio::spawn(async move {
            let mut ticker = tokio::time::interval(Duration::from_secs(30));
            ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
            loop {
                ticker.tick().await;
                let config = tasks::news_load(&store).await;
                if !tasks::scan_running()
                    && tasks::news_is_due(&store, &config, chrono::Utc::now()).await
                {
                    if let Err(error) =
                        tasks::run_news_scan(&store, &media, &config_dir, "schedule", None).await
                    {
                        tracing::warn!(%error, "scheduled Past Tense News scan failed");
                    }
                }
            }
        });
    }

    // And the Local Files scan on its own daily timer: it only touches the
    // local connections, so the media-server walks never delay it.
    {
        let store = store.clone();
        let media = media_sources.clone();
        let config_dir = config.clone();
        tokio::spawn(async move {
            let mut ticker = tokio::time::interval(Duration::from_secs(30));
            ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
            loop {
                ticker.tick().await;
                let config = tasks::local_load(&store).await;
                if !tasks::scan_running()
                    && tasks::local_is_due(&store, &config, chrono::Utc::now()).await
                {
                    if let Err(error) =
                        tasks::run_local_scan(&store, &media, &config_dir, "schedule", None).await
                    {
                        tracing::warn!(%error, "scheduled Local Files scan failed");
                    }
                }
            }
        });
    }

    // The encoder supervisor: next runs beside ChannelFlow and its HLS is
    // proxied through our own origin. When the binary is absent the live paths
    // keep answering "not encoding yet".
    let lineup_path = store.config_dir().join("next").join("lineup.json");
    let encoder = Arc::new(encoder::Encoder::new(lineup_path, 8409));

    axum::serve(
        listener,
        api::router(store, about, plugins, media_sources, http, encoder, plugin_routers),
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