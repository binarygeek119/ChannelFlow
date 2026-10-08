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
    /// This channel's transcode overrides: a sparse patch over the instance
    /// defaults in `transcode.json`, deep-merged by
    /// [`TranscodeConfig::merged`](crate::transcode::TranscodeConfig::merged).
    ///
    /// An absent key inherits from the defaults; a key present with the value
    /// `null` is a real setting ("software encode", "automatic bitrate"), not
    /// an instruction to inherit. Empty means the channel follows the
    /// Transcode page exactly, which is why it is omitted when empty — an
    /// untouched channel document stays byte-identical.
    #[serde(default = "empty_object", skip_serializing_if = "is_empty_object")]
    pub transcode: serde_json::Value,
    pub created_at: DateTime<Utc>,
    pub updated_at: DateTime<Utc>,
}

fn empty_object() -> serde_json::Value {
    serde_json::Value::Object(serde_json::Map::new())
}

fn is_empty_object(value: &serde_json::Value) -> bool {
    value.as_object().is_some_and(|map| map.is_empty())
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
