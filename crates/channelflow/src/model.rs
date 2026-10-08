//! The channel records ChannelFlow stores and serves.
//!
//! These are ChannelFlow's own model, deliberately separate from the playout
//! documents under `vendor/ersatztv-next/schema/`. Turning a `Channel` into
//! next's `channel.json` + `playout.json` is a later step.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Channel {
    pub id: Uuid,
    pub number: u32,
    pub name: String,
    #[serde(default)]
    pub description: String,
    #[serde(default)]
    pub enabled: bool,
    pub created_at: DateTime<Utc>,
    pub updated_at: DateTime<Utc>,
}

#[derive(Debug, Deserialize)]
pub struct NewChannel {
    pub number: u32,
    pub name: String,
    #[serde(default)]
    pub description: String,
    #[serde(default = "enabled_by_default")]
    pub enabled: bool,
}

fn enabled_by_default() -> bool {
    true
}

#[derive(Debug, Deserialize)]
pub struct UpdateChannel {
    pub number: Option<u32>,
    pub name: Option<String>,
    pub description: Option<String>,
    pub enabled: Option<bool>,
}
