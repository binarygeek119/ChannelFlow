//! Connection settings for OpenAI, or anything else that speaks its API.
//!
//! The AI page is one instance-wide block stored at `<config>/ai.json`. There
//! is deliberately no "OpenAI vs Venice" choice here: the base URL *is* the
//! choice, so a compatible provider or a model on your own network is reached
//! by pointing this somewhere else rather than by waiting for a code change.
//!
//! Two models are named, because the API uses different ones for different
//! jobs: `chat_model` for text — the lineup and guide copy the playout
//! milestone will ask for — and `tts_model` plus `voice` for speech.

use serde::{Deserialize, Serialize};
use serde_json::Value;

/// Settings that cannot be used as typed.
#[derive(Debug)]
pub struct AiError(String);

impl std::fmt::Display for AiError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for AiError {}

/// An OpenAI-compatible endpoint and the chat and text-to-speech settings used
/// with it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AiConfig {
    /// API root, e.g. `https://api.openai.com/v1`.
    #[serde(default = "default_base_url")]
    pub base_url: String,
    /// Bearer token. Empty means the endpoint needs no key — a real setting,
    /// not "unset" — which is the normal case for a model on the local network.
    #[serde(default)]
    pub api_key: String,
    /// The chat engine to ask for text, e.g. `gpt-4o-mini`. Nothing calls it
    /// yet — lineup generation lands with playout — but naming it here keeps
    /// the whole endpoint configured in one place.
    #[serde(default = "default_chat_model")]
    pub chat_model: String,
    /// The speech engine to ask for, e.g. `tts-1` or `gpt-4o-mini-tts`.
    #[serde(default = "default_tts_model")]
    pub tts_model: String,
    /// The voice id to speak with, e.g. `nova`.
    #[serde(default = "default_voice")]
    pub voice: String,
}

impl Default for AiConfig {
    fn default() -> Self {
        Self {
            base_url: default_base_url(),
            api_key: String::new(),
            chat_model: default_chat_model(),
            tts_model: default_tts_model(),
            voice: default_voice(),
        }
    }
}

fn default_base_url() -> String {
    "https://api.openai.com/v1".to_string()
}

fn default_chat_model() -> String {
    "gpt-4o-mini".to_string()
}

fn default_tts_model() -> String {
    "tts-1".to_string()
}

fn default_voice() -> String {
    "nova".to_string()
}

/// What `GET /api/ai` — and the result of a save — returns. The key itself is
/// never sent back, only whether one is saved: this API has no authentication
/// and the server listens on every interface by default.
#[derive(Debug, Serialize)]
pub struct AiView {
    pub base_url: String,
    pub api_key_set: bool,
    pub chat_model: String,
    pub tts_model: String,
    pub voice: String,
}

impl AiConfig {
    pub fn view(&self) -> AiView {
        AiView {
            base_url: self.base_url.clone(),
            api_key_set: !self.api_key.is_empty(),
            chat_model: self.chat_model.clone(),
            tts_model: self.tts_model.clone(),
            voice: self.voice.clone(),
        }
    }

    /// Read a whole on-disk document.
    pub fn parse(value: &Value) -> Result<Self, AiError> {
        let config: AiConfig = serde_json::from_value(value.clone())
            .map_err(|error| AiError(format!("not a valid AI settings document: {error}")))?;
        config.validate()
    }

    /// Resolve a partial update on top of these settings, then validate.
    ///
    /// A `base_url`, `chat_model`, `tts_model` or `voice` that a request leaves
    /// out keeps what is stored. `api_key` absent keeps the saved key; present
    /// — even empty — replaces it, which is how the page clears a key without
    /// a second endpoint, and the only way to tell "leave it" from "remove it"
    /// when `null` would otherwise mean both.
    pub fn apply(&self, value: &Value) -> Result<Self, AiError> {
        #[derive(Deserialize)]
        #[serde(deny_unknown_fields)]
        struct Update {
            #[serde(default)]
            base_url: Option<String>,
            #[serde(default)]
            api_key: Option<String>,
            #[serde(default)]
            chat_model: Option<String>,
            #[serde(default)]
            tts_model: Option<String>,
            #[serde(default)]
            voice: Option<String>,
        }

        let update: Update = serde_json::from_value(value.clone())
            .map_err(|error| AiError(format!("not a valid AI settings update: {error}")))?;

        let mut next = self.clone();
        if let Some(base_url) = update.base_url {
            next.base_url = base_url;
        }
        if let Some(api_key) = update.api_key {
            next.api_key = api_key;
        }
        if let Some(chat_model) = update.chat_model {
            next.chat_model = chat_model;
        }
        if let Some(tts_model) = update.tts_model {
            next.tts_model = tts_model;
        }
        if let Some(voice) = update.voice {
            next.voice = voice;
        }
        next.validate()
    }

    /// Trim what a form adds by accident, then check the things that must be
    /// non-empty. The missing scheme is the mistake worth catching:
    /// `api.openai.com/v1` reads fine and cannot be requested.
    fn validate(mut self) -> Result<Self, AiError> {
        self.base_url = self.base_url.trim().trim_end_matches('/').to_string();
        if !(self.base_url.starts_with("http://") || self.base_url.starts_with("https://")) {
            return Err(AiError(
                "API URL must start with http:// or https://, for example https://api.openai.com/v1"
                    .to_string(),
            ));
        }
        self.api_key = self.api_key.trim().to_string();
        self.chat_model = self.chat_model.trim().to_string();
        if self.chat_model.is_empty() {
            return Err(AiError(
                "chat model cannot be empty, for example gpt-4o-mini".to_string(),
            ));
        }
        self.tts_model = self.tts_model.trim().to_string();
        if self.tts_model.is_empty() {
            return Err(AiError("TTS model cannot be empty, for example tts-1".to_string()));
        }
        self.voice = self.voice.trim().to_string();
        if self.voice.is_empty() {
            return Err(AiError("voice cannot be empty, for example nova".to_string()));
        }
        Ok(self)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn defaults_point_at_openai() {
        let config = AiConfig::default();
        assert_eq!(config.base_url, "https://api.openai.com/v1");
        assert_eq!(config.chat_model, "gpt-4o-mini");
        assert_eq!(config.tts_model, "tts-1");
        assert_eq!(config.voice, "nova");
        assert!(!config.view().api_key_set);
    }

    #[test]
    fn an_update_only_touches_the_fields_it_names() {
        let stored = AiConfig {
            api_key: "sk-secret".to_string(),
            ..AiConfig::default()
        };
        let updated = stored.apply(&json!({ "voice": "alloy" })).unwrap();
        assert_eq!(updated.voice, "alloy");
        assert_eq!(updated.api_key, "sk-secret", "an absent key keeps the saved one");
        assert_eq!(updated.base_url, "https://api.openai.com/v1");
        assert_eq!(updated.chat_model, "gpt-4o-mini");
    }

    #[test]
    fn a_saved_document_without_a_chat_model_gets_the_default() {
        // ai.json written before the chat model existed must still load.
        let config = AiConfig::parse(&json!({
            "base_url": "https://example.test/v1",
            "api_key": "",
            "tts_model": "tts-1",
            "voice": "nova"
        }))
        .unwrap();
        assert_eq!(config.chat_model, "gpt-4o-mini");
    }

    #[test]
    fn an_empty_key_clears_it() {
        let stored = AiConfig {
            api_key: "sk-secret".to_string(),
            ..AiConfig::default()
        };
        assert_eq!(stored.apply(&json!({ "api_key": "" })).unwrap().api_key, "");
    }

    #[test]
    fn the_key_is_never_part_of_the_view() {
        let config = AiConfig {
            api_key: "sk-secret".to_string(),
            ..AiConfig::default()
        };
        let view = serde_json::to_value(config.view()).unwrap();
        assert_eq!(view["api_key_set"], json!(true));
        assert!(view.get("api_key").is_none());
    }

    #[test]
    fn trims_the_url_and_requires_a_scheme() {
        let config = AiConfig::default()
            .apply(&json!({ "base_url": "  https://example.test/v1/  " }))
            .unwrap();
        assert_eq!(config.base_url, "https://example.test/v1");

        assert!(AiConfig::default()
            .apply(&json!({ "base_url": "api.openai.com/v1" }))
            .is_err());
    }

    #[test]
    fn rejects_empty_models_and_voice() {
        assert!(AiConfig::default().apply(&json!({ "chat_model": "  " })).is_err());
        assert!(AiConfig::default().apply(&json!({ "tts_model": "  " })).is_err());
        assert!(AiConfig::default().apply(&json!({ "voice": "" })).is_err());
    }

    #[test]
    fn rejects_unknown_fields() {
        assert!(AiConfig::default().apply(&json!({ "model": "gpt-4o" })).is_err());
    }
}
