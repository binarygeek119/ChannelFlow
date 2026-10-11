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
    /// Dial number; a fractional value denotes a subchannel (119.1).
    #[serde(with = "number_fmt")]
    pub number: f64,
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
    #[serde(with = "number_fmt")]
    pub number: f64,
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
    #[serde(default, deserialize_with = "option_number_fmt::deserialize")]
    pub number: Option<f64>,
    pub name: Option<String>,
    pub description: Option<String>,
    pub enabled: Option<bool>,
}

/// Channel dial numbers: whole numbers stay whole (`119`, not `119.0`), while
/// subchannels keep their fractional part (`119.1`). Clients may also send the
/// number as a string.
mod number_fmt {
    use serde::{Deserialize, Deserializer, Serializer};

    pub fn serialize<S>(value: &f64, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        if value.fract() == 0.0 && value.is_finite() {
            serializer.serialize_i64(*value as i64)
        } else {
            serializer.serialize_f64(*value)
        }
    }

    pub fn deserialize<'de, D>(deserializer: D) -> Result<f64, D::Error>
    where
        D: Deserializer<'de>,
    {
        #[derive(Deserialize)]
        #[serde(untagged)]
        enum Number {
            Int(i64),
            Float(f64),
            Str(String),
        }
        match Number::deserialize(deserializer)? {
            Number::Int(value) => Ok(value as f64),
            Number::Float(value) => Ok(value),
            Number::Str(value) => value
                .parse::<f64>()
                .map_err(|_| serde::de::Error::custom("channel number must be a number")),
        }
    }
}

/// Deserializer for an optional dial number, accepting numbers or strings.
mod option_number_fmt {
    use serde::{Deserialize, Deserializer};

    pub fn deserialize<'de, D>(deserializer: D) -> Result<Option<f64>, D::Error>
    where
        D: Deserializer<'de>,
    {
        #[derive(Deserialize)]
        #[serde(untagged)]
        enum Number {
            Int(i64),
            Float(f64),
            Str(String),
            Null,
        }
        match Number::deserialize(deserializer)? {
            Number::Int(value) => Ok(Some(value as f64)),
            Number::Float(value) => Ok(Some(value)),
            Number::Str(value) => value
                .parse::<f64>()
                .map(Some)
                .map_err(|_| serde::de::Error::custom("channel number must be a number")),
            Number::Null => Ok(None),
        }
    }
}
