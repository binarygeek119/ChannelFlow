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
    /// The channel's on-air watermark: an overlay image and how the encoder
    /// places and fades it. Absent until one is uploaded.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub watermark: Option<Watermark>,
    /// Instructions for AI channel scheduling - how the lineup should pick
    /// and order media for this channel. Empty until one is written.
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub ai_prompt: String,
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

/// A channel's watermark: an on-air overlay image and the settings the encoder
/// applies when playout runs. Everything here is stored; only the image bytes
/// themselves are handled by the API (decoded from `image_data` and written to
/// the images store).
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(default)]
pub struct Watermark {
    /// Images-root-relative path of the uploaded artwork, e.g.
    /// `watermarks/1c9a….png`. Absent until an image is uploaded.
    pub image_path: Option<String>,
    /// Where the watermark sits: "bottom-right", "bottom-left", "top-left",
    /// or "top-right".
    pub position: String,
    /// Pixels to nudge the watermark away from its default corner placement.
    pub offset_x: i32,
    pub offset_y: i32,
    /// Scale as a percentage of the uploaded size (50–200, 100 = original).
    pub scale: f64,
    /// See-through level: 0 (invisible) to 100 (fully opaque).
    pub opacity: u8,
    /// Fade the watermark out just before a commercial break starts.
    pub fade_before_break: bool,
    /// Fade the watermark back in once the show returns after the break.
    pub fade_back_after_break: bool,
    /// Seconds after the show returns before the fade-in starts.
    pub fade_back_delay_seconds: f64,
    /// How long the fade out / fade in takes, in seconds.
    pub fade_duration_seconds: f64,
    /// Base64 PNG/JPEG from the upload form. Written to disk by the API and
    /// never stored in the channel document.
    #[serde(default, skip_serializing)]
    pub image_data: Option<String>,
}

impl Default for Watermark {
    fn default() -> Self {
        Self {
            image_path: None,
            position: "bottom-right".to_string(),
            offset_x: 12,
            offset_y: 12,
            scale: 100.0,
            opacity: 100,
            fade_before_break: false,
            fade_back_after_break: false,
            fade_back_delay_seconds: 5.0,
            fade_duration_seconds: 1.5,
            image_data: None,
        }
    }
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
    #[serde(default)]
    pub watermark: Option<Watermark>,
    #[serde(default)]
    pub ai_prompt: String,
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
    /// `Some(Some(w))` sets the watermark, `Some(None)` clears it, absent
    /// leaves it untouched (serde can't tell absent from `null`, so this uses
    /// a deserializer that can).
    #[serde(default, deserialize_with = "option_watermark::deserialize")]
    pub watermark: Option<Option<Watermark>>,
    #[serde(default)]
    pub ai_prompt: Option<String>,
}

mod option_watermark {
    use super::Watermark;
    use serde::{Deserialize, Deserializer};

    #[derive(Deserialize)]
    #[serde(untagged)]
    enum Help {
        Value(Watermark),
        Null,
    }

    pub fn deserialize<'de, D>(deserializer: D) -> Result<Option<Option<Watermark>>, D::Error>
    where
        D: Deserializer<'de>,
    {
        match Help::deserialize(deserializer)? {
            Help::Value(watermark) => Ok(Some(Some(watermark))),
            Help::Null => Ok(Some(None)),
        }
    }
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
