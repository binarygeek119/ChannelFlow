mod ai;
mod api;
mod model;
mod openai;
mod store;
mod transcode;

use std::net::SocketAddr;
use std::path::PathBuf;

use anyhow::{Context, Result};
use clap::Parser;
use tracing_subscriber::EnvFilter;

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

    let store = if args.database_url.trim().is_empty() {
        Store::open(&config).with_context(|| {
            format!(
                "opening the channel store under {} — that directory must be writable by uid 1000 (`ersatztv`)",
                config.display()
            )
        })?
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
    store.seed().await?;

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
    let http = openai::client().context("building the HTTP client for AI requests")?;
    axum::serve(listener, api::router(store, about, http)).await?;
    Ok(())
}

/// Show a connection string without its password in logs and errors.
fn redact_url(url: &str) -> String {
    let mut shown: String = url
        .split('@')
        .last()
        .unwrap_or(url)
        .chars()
        .take(120)
        .collect();
    if url.contains('@') {
        shown = format!("<credentials>@{shown}");
    }
    shown
}
