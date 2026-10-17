//! Read-only access to the core's own data, for plugins that let people
//! configure core-owned things (channels, lineups) per channel.
//!
//! Only the lightweight view the plugin UIs need is exposed, and nothing a
//! plugin can write through here — writes stay core-owned or go through the
//! plugin's own storage. The permission that gates it is `api:core:read`.

use async_trait::async_trait;

/// One channel, as the core sees it — no transcode patch, no timestamps.
#[derive(Debug, Clone, serde::Serialize)]
pub struct CoreChannel {
    pub id: String,
    /// Dial number; a fractional value denotes a subchannel (119.1).
    #[serde(with = "number_fmt")]
    pub number: f64,
    pub name: String,
    pub enabled: bool,
}

/// Serialize whole channel numbers without the trailing `.0`.
mod number_fmt {
    use serde::{self, Serializer};

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
}

/// A core-data call failed.
#[derive(Debug, Clone)]
pub struct CoreDataError(pub String);

impl std::fmt::Display for CoreDataError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for CoreDataError {}

/// An OpenAI-compatible TTS endpoint configuration, shared across plugins.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct TtsProvider {
    pub base_url: String,
    pub api_key: String,
    pub tts_model: String,
    pub voice: String,
}

/// An OpenAI-compatible chat endpoint configuration, shared across plugins.
/// A plugin that wants text written (a newscast rewritten, a lineup drafted)
/// asks the core for one of these rather than storing its own endpoint, so the
/// AI page stays the one place providers are configured.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct LlmProvider {
    /// The provider's id on the AI page, so a caller can report which one it used.
    pub id: String,
    /// The provider's display name.
    pub name: String,
    pub base_url: String,
    pub api_key: String,
    pub chat_model: String,
    /// An optional sampling temperature for scheduling (kept low).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub temperature: Option<f32>,
}

/// A filter a scheduling plugin narrows the base catalog with before it ever
/// reaches a model. Every field is optional; the default means "everything".
#[derive(Debug, Clone, Default, serde::Serialize, serde::Deserialize)]
pub struct CatalogFilter {
    /// Coarse kinds: `movie`, `series`, `episode`, `musicvideo`, … Empty means
    /// every kind, so a "TV and movies" pool can name both.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub kinds: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub genres: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub tags: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub year_min: Option<i32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub year_max: Option<i32>,
    /// An official-rating ceiling (the caller knows its own ordinal order).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub rating_max: Option<String>,
    /// Explicit catalog keys (pins, list membership).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub match_keys: Vec<String>,
    /// Cap on how many rows come back (the caller's pool size).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub limit: Option<u32>,
}

/// One catalog row, as a scheduling plugin sees it. It carries no file path:
/// resolve that with [`CoreData::file_for`] so the media source that owns the
/// file stays authoritative.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct CatalogRow {
    pub match_key: String,
    pub kind: String,
    pub title: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub year: Option<i32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub runtime_ms: Option<i64>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub genres: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub official_rating: Option<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub tags: Vec<String>,
    /// For an episode: the series' catalog key and the season/episode numbers.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub series_key: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub season: Option<i32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub episode: Option<i32>,
}

/// A playable file for a catalog item, resolved through the source that owns
/// it. `path` is what next's `LocalSource` gets.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct MediaFile {
    pub source_kind: String,
    pub path: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub runtime_ms: Option<i64>,
}

/// One message in a chat request.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct ChatMessage {
    pub role: String,
    pub content: String,
}

/// A chat completion request, walked across the AI page's providers by the core.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct ChatRequest {
    pub messages: Vec<ChatMessage>,
    /// Overrides the provider's own temperature when set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub temperature: Option<f32>,
    /// Ask for a JSON object reply where the provider supports it.
    #[serde(default)]
    pub json: bool,
    /// A per-call model override; normally the provider's `chat_model`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
}

/// The answer to a [`ChatRequest`], plus which provider produced it.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct ChatResponse {
    pub content: String,
    pub provider_id: String,
    /// Providers that were skipped, and why, so a UI can explain a fallback.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub attempts: Vec<String>,
}

/// What the core hands a plugin that asked for `api:core:read`.
#[async_trait]
pub trait CoreData: Send + Sync {
    /// Every channel, ordered by number then name.
    async fn channels(&self) -> Result<Vec<CoreChannel>, CoreDataError>;
    /// The ids of the plugins installed in the registry (e.g. to decide which
    /// contributed presets to show).
    async fn installed_plugins(&self) -> Result<Vec<String>, CoreDataError>;
    /// Replace the guide programmes a channel shows. A plugin that produces
    /// scheduled blocks (a weather forecast, say) writes them here; the core
    /// keeps them and serves the guide. Programmes for other channels are
    /// left alone. The default is a no-op so read-only plugins need not
    /// implement it.
    async fn set_channel_guide(
        &self,
        channel_id: &str,
        programs: Vec<serde_json::Value>,
    ) -> Result<(), CoreDataError> {
        let _ = (channel_id, programs);
        Ok(())
    }

    /// Replace the channel's lineup (the 48-slot schedule the Lineups page shows).
    /// The Scheduler writes its generated blocks here so the Lineups page can
    /// display what was scheduled. The default is a no-op so read-only plugins
    /// need not implement it.
    async fn set_channel_lineup(
        &self,
        channel_id: &str,
        slots: Vec<serde_json::Value>,
    ) -> Result<(), CoreDataError> {
        let _ = (channel_id, slots);
        Ok(())
    }
    /// The first AI provider suitable for TTS.
    ///
    /// Priority:
    /// 1. Enabled provider with `tts_only == true`.
    /// 2. Enabled provider with a non-empty `tts_model`.
    ///
    /// Returns `None` if the AI plugin is not installed or no suitable
    /// provider is configured.
    async fn tts_provider(&self) -> Result<Option<TtsProvider>, CoreDataError> {
        Ok(None)
    }
    /// The first AI provider suitable for writing text.
    ///
    /// Priority: the lowest-numbered enabled provider that is not `tts_only`
    /// and names a non-empty `chat_model`. Returns `None` if the AI plugin is
    /// not installed or no chat provider is configured.
    async fn llm_provider(&self) -> Result<Option<LlmProvider>, CoreDataError> {
        Ok(None)
    }

    /// The base catalog, filtered. The scheduler's candidate pool comes from
    /// here. Requires `api:core:read`.
    async fn catalog(&self, filter: CatalogFilter) -> Result<Vec<CatalogRow>, CoreDataError> {
        let _ = filter;
        Ok(Vec::new())
    }

    /// The channel's saved roster (the base `ChannelLineup`) as JSON — the
    /// scheduling plugin's layer-2 seed. `None` when the channel has none.
    async fn lineup(&self, channel_id: &str) -> Result<Option<serde_json::Value>, CoreDataError> {
        let _ = channel_id;
        Ok(None)
    }

    /// Every enabled provider suitable for scheduling, in failover order.
    /// `tts_only` providers are excluded; `scheduling_only` ones are included.
    async fn llm_providers(&self) -> Result<Vec<LlmProvider>, CoreDataError> {
        Ok(Vec::new())
    }

    /// One chat completion, walking the providers in order. The core owns the
    /// failover so plugins never re-implement it.
    async fn chat(&self, request: ChatRequest) -> Result<ChatResponse, CoreDataError> {
        let _ = request;
        Err(CoreDataError("chat is not available".to_string()))
    }

    /// The playable file for a catalog item, resolved from the base's own
    /// `media_files` table.
    async fn file_for(&self, match_key: &str) -> Result<Option<MediaFile>, CoreDataError> {
        let _ = match_key;
        Ok(None)
    }
}

/// A test double that answers with no channels.
#[derive(Debug, Default)]
pub struct NoCoreData;

#[async_trait]
impl CoreData for NoCoreData {
    async fn channels(&self) -> Result<Vec<CoreChannel>, CoreDataError> {
        Ok(Vec::new())
    }

    async fn installed_plugins(&self) -> Result<Vec<String>, CoreDataError> {
        Ok(Vec::new())
    }
}

/// An in-memory double seeded with channels, for plugin tests.
#[derive(Debug, Default)]
pub struct InMemoryCoreData {
    inner: std::sync::Mutex<Vec<CoreChannel>>,
}

impl InMemoryCoreData {
    pub fn new(channels: Vec<CoreChannel>) -> Self {
        Self {
            inner: std::sync::Mutex::new(channels),
        }
    }
}

#[async_trait]
impl CoreData for InMemoryCoreData {
    async fn channels(&self) -> Result<Vec<CoreChannel>, CoreDataError> {
        Ok(self.inner.lock().expect("lock").clone())
    }

    async fn installed_plugins(&self) -> Result<Vec<String>, CoreDataError> {
        Ok(Vec::new())
    }
}