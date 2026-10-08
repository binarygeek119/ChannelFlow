//! Connection settings for OpenAI, or anything else that speaks its API.
//!
//! The AI Suite plugin's provider list. Each provider is a whole
//! OpenAI-compatible connection: a name, a priority, and the URL, key and
//! models to use with it. There is deliberately no "OpenAI vs Venice" choice —
//! the base URL *is* the choice, so a compatible provider or a model on your
//! own network is reached by pointing one of these somewhere else.
//!
//! Providers are tried in priority order, lowest number first, and the app
//! moves on to the next when one cannot be reached. That is why priority is
//! unique: two providers sharing a number would leave the order to chance.
//!
//! The provider list is persisted by the plugin itself through its namespaced
//! storage; this module only models and validates it.

use serde::{Deserialize, Serialize};
use serde_json::Value;
use uuid::Uuid;

/// Settings that cannot be used as typed.
#[derive(Debug)]
pub struct AiError(String);

impl std::fmt::Display for AiError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for AiError {}

/// One OpenAI-compatible endpoint and the models to use with it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AiProvider {
    /// Stable identity, assigned when the provider is created. The page
    /// addresses a provider by this rather than by name, so renaming one does
    /// not move it to a different tab.
    pub id: String,
    /// The tab's title. Unique across providers.
    #[serde(default)]
    pub name: String,
    /// Failover order: the lowest number is tried first. Unique.
    #[serde(default)]
    pub priority: u32,
    /// API root, e.g. `https://api.openai.com/v1`.
    #[serde(default = "default_base_url")]
    pub base_url: String,
    /// Bearer token. Empty means the endpoint needs no key — a real setting,
    /// not "unset" — which is the normal case for a model on the local network.
    #[serde(default)]
    pub api_key: String,
    /// The chat engine to ask for text, e.g. `gpt-4o-mini`. Nothing calls it
    /// yet — lineup generation lands with playout — but naming it here keeps
    /// each endpoint configured in one place.
    #[serde(default = "default_chat_model")]
    pub chat_model: String,
    /// The speech engine to ask for, e.g. `tts-1` or `gpt-4o-mini-tts`.
    #[serde(default = "default_tts_model")]
    pub tts_model: String,
    /// The voice id to speak with, e.g. `nova`.
    #[serde(default = "default_voice")]
    pub voice: String,
}

/// The whole AI page: the saved providers plus the values a new one starts
/// from. An empty list is a valid configuration — nothing uses AI until a
/// provider is added.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub struct AiConfig {
    #[serde(default)]
    pub providers: Vec<AiProvider>,
}

/// The values the "New provider" form starts from. Served rather than baked
/// into the browser so the OpenAI defaults live in one place.
#[derive(Debug, Serialize)]
pub struct ProviderDefaults {
    pub base_url: String,
    pub chat_model: String,
    pub tts_model: String,
    pub voice: String,
}

/// What `GET /api/ai` — and the result of a save — returns. A saved key is
/// never sent back, only whether one is set: this API has no authentication
/// and the server listens on every interface by default.
#[derive(Debug, Serialize)]
pub struct AiView {
    pub providers: Vec<ProviderView>,
    /// The next free priority, so a new provider does not collide by accident.
    pub next_priority: u32,
    pub defaults: ProviderDefaults,
}

#[derive(Debug, Serialize)]
pub struct ProviderView {
    pub id: String,
    pub name: String,
    pub priority: u32,
    pub base_url: String,
    pub api_key_set: bool,
    pub chat_model: String,
    pub tts_model: String,
    pub voice: String,
}

impl AiProvider {
    pub fn view(&self) -> ProviderView {
        ProviderView {
            id: self.id.clone(),
            name: self.name.clone(),
            priority: self.priority,
            base_url: self.base_url.clone(),
            api_key_set: !self.api_key.is_empty(),
            chat_model: self.chat_model.clone(),
            tts_model: self.tts_model.clone(),
            voice: self.voice.clone(),
        }
    }
}

impl AiConfig {
    pub fn view(&self) -> AiView {
        AiView {
            providers: self.ordered().into_iter().map(AiProvider::view).collect(),
            next_priority: self.next_priority(),
            defaults: provider_defaults(),
        }
    }

    /// Providers in the order the app tries them: lowest priority first, then
    /// by name so a hand-edited file with equal numbers still has a stable
    /// order. A save cannot create equal numbers; see `ensure_free`.
    pub fn ordered(&self) -> Vec<&AiProvider> {
        let mut providers: Vec<&AiProvider> = self.providers.iter().collect();
        providers.sort_by(|a, b| a.priority.cmp(&b.priority).then_with(|| a.name.cmp(&b.name)));
        providers
    }

    /// The first number not in use, so a new provider does not collide.
    pub fn next_priority(&self) -> u32 {
        let mut candidate = 1;
        while self.providers.iter().any(|provider| provider.priority == candidate) {
            candidate += 1;
        }
        candidate
    }

    pub fn find(&self, id: &str) -> Option<&AiProvider> {
        self.providers.iter().find(|provider| provider.id == id)
    }

    fn get(&self, id: &str) -> Result<&AiProvider, AiError> {
        self.find(id)
            .ok_or_else(|| AiError(format!("no provider with id {id}")))
    }

    /// Add a provider from a create body. Its fields are validated and its
    /// name and priority checked against the providers already saved.
    pub fn create(&mut self, body: &Value) -> Result<AiProvider, AiError> {
        let provider = new_provider(body)?;
        self.ensure_free(&provider, None)?;
        self.providers.push(provider.clone());
        Ok(provider)
    }

    /// A provider built from a create body without storing it, for the "New
    /// provider" tab's test: the values can be checked before they are saved.
    pub fn draft(body: &Value) -> Result<AiProvider, AiError> {
        new_provider(body)
    }

    /// Apply a partial update to a stored provider. A field left out keeps
    /// what is stored; `api_key` left out keeps the saved key, while present —
    /// even empty — replaces it, which is the only way to tell "leave it" from
    /// "remove it" when `null` would otherwise mean both.
    pub fn update(&mut self, id: &str, body: &Value) -> Result<AiProvider, AiError> {
        let candidate = cleaned(merge(self.get(id)?.clone(), body)?)?;
        self.ensure_free(&candidate, Some(id))?;
        if let Some(slot) = self.providers.iter_mut().find(|provider| provider.id == id) {
            *slot = candidate.clone();
        }
        Ok(candidate)
    }

    /// The provider a test of `id` would use: the stored one with the form's
    /// values laid over it and validated, but not checked against the other
    /// providers — a test may run before a conflicting save is resolved.
    pub fn resolve(&self, id: &str, body: &Value) -> Result<AiProvider, AiError> {
        cleaned(merge(self.get(id)?.clone(), body)?)
    }

    pub fn delete(&mut self, id: &str) -> Result<(), AiError> {
        let before = self.providers.len();
        self.providers.retain(|provider| provider.id != id);
        if self.providers.len() == before {
            return Err(AiError(format!("no provider with id {id}")));
        }
        Ok(())
    }

    /// Read a whole on-disk document, upgrading the single-endpoint shape
    /// 2.0.0 wrote before providers existed.
    pub fn parse(value: &Value) -> Result<Self, AiError> {
        let config = if value.get("providers").is_some() {
            serde_json::from_value::<AiConfig>(value.clone())
                .map_err(|error| AiError(format!("not a valid AI settings document: {error}")))?
        } else if value.get("base_url").is_some() {
            migrate(value)?
        } else {
            return Err(AiError(
                "not a valid AI settings document: expected a `providers` list".to_string(),
            ));
        };
        config.validate()?;
        Ok(config)
    }

    /// Every stored provider has to stand on its own, and no two may share a
    /// name or a priority.
    fn validate(&self) -> Result<(), AiError> {
        for provider in &self.providers {
            cleaned(provider.clone())?;
        }
        for provider in &self.providers {
            self.ensure_free(provider, Some(&provider.id))?;
        }
        Ok(())
    }

    /// Reject a name or priority another provider already owns.
    fn ensure_free(&self, candidate: &AiProvider, ignore: Option<&str>) -> Result<(), AiError> {
        for existing in &self.providers {
            if Some(existing.id.as_str()) == ignore {
                continue;
            }
            if existing.priority == candidate.priority {
                return Err(AiError(format!(
                    "priority {} is already used by \"{}\" — every provider needs its own number",
                    candidate.priority, existing.name
                )));
            }
            if existing.name.eq_ignore_ascii_case(&candidate.name) {
                return Err(AiError(format!(
                    "a provider named \"{}\" already exists",
                    existing.name
                )));
            }
        }
        Ok(())
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

fn provider_defaults() -> ProviderDefaults {
    ProviderDefaults {
        base_url: default_base_url(),
        chat_model: default_chat_model(),
        tts_model: default_tts_model(),
        voice: default_voice(),
    }
}

/// Build a new provider from a create body, with a fresh id.
fn new_provider(body: &Value) -> Result<AiProvider, AiError> {
    #[derive(Deserialize)]
    #[serde(deny_unknown_fields)]
    struct New {
        name: String,
        priority: u32,
        #[serde(default = "default_base_url")]
        base_url: String,
        #[serde(default)]
        api_key: String,
        #[serde(default = "default_chat_model")]
        chat_model: String,
        #[serde(default = "default_tts_model")]
        tts_model: String,
        #[serde(default = "default_voice")]
        voice: String,
    }

    let input: New = serde_json::from_value(body.clone())
        .map_err(|error| AiError(format!("not a valid provider: {error}")))?;
    cleaned(AiProvider {
        id: Uuid::new_v4().to_string(),
        name: input.name,
        priority: input.priority,
        base_url: input.base_url,
        api_key: input.api_key,
        chat_model: input.chat_model,
        tts_model: input.tts_model,
        voice: input.voice,
    })
}

/// Lay a partial update over a stored provider. Uniqueness is checked by the
/// caller, against the other providers.
fn merge(mut provider: AiProvider, body: &Value) -> Result<AiProvider, AiError> {
    #[derive(Deserialize)]
    #[serde(deny_unknown_fields)]
    struct Update {
        #[serde(default)]
        name: Option<String>,
        #[serde(default)]
        priority: Option<u32>,
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

    let update: Update = serde_json::from_value(body.clone())
        .map_err(|error| AiError(format!("not a valid provider update: {error}")))?;
    if let Some(name) = update.name {
        provider.name = name;
    }
    if let Some(priority) = update.priority {
        provider.priority = priority;
    }
    if let Some(base_url) = update.base_url {
        provider.base_url = base_url;
    }
    if let Some(api_key) = update.api_key {
        provider.api_key = api_key;
    }
    if let Some(chat_model) = update.chat_model {
        provider.chat_model = chat_model;
    }
    if let Some(tts_model) = update.tts_model {
        provider.tts_model = tts_model;
    }
    if let Some(voice) = update.voice {
        provider.voice = voice;
    }
    Ok(provider)
}

/// Trim what a form adds by accident, then check the things that must be
/// non-empty. The missing scheme is the mistake worth catching:
/// `api.openai.com/v1` reads fine and cannot be requested.
fn cleaned(mut provider: AiProvider) -> Result<AiProvider, AiError> {
    provider.name = provider.name.trim().to_string();
    if provider.name.is_empty() {
        return Err(AiError("provider name cannot be empty".to_string()));
    }
    provider.base_url = provider.base_url.trim().trim_end_matches('/').to_string();
    if !(provider.base_url.starts_with("http://") || provider.base_url.starts_with("https://")) {
        return Err(AiError(
            "API URL must start with http:// or https://, for example https://api.openai.com/v1"
                .to_string(),
        ));
    }
    provider.api_key = provider.api_key.trim().to_string();
    provider.chat_model = provider.chat_model.trim().to_string();
    if provider.chat_model.is_empty() {
        return Err(AiError(
            "chat model cannot be empty, for example gpt-4o-mini".to_string(),
        ));
    }
    provider.tts_model = provider.tts_model.trim().to_string();
    if provider.tts_model.is_empty() {
        return Err(AiError("TTS model cannot be empty, for example tts-1".to_string()));
    }
    provider.voice = provider.voice.trim().to_string();
    if provider.voice.is_empty() {
        return Err(AiError("voice cannot be empty, for example nova".to_string()));
    }
    Ok(provider)
}

/// The single global endpoint 2.0.0 stored before this list existed. Fold it
/// into one provider so an existing `ai.json` keeps working.
fn migrate(value: &Value) -> Result<AiConfig, AiError> {
    #[derive(Deserialize)]
    #[serde(deny_unknown_fields)]
    struct Legacy {
        #[serde(default = "default_base_url")]
        base_url: String,
        #[serde(default)]
        api_key: String,
        #[serde(default = "default_chat_model")]
        chat_model: String,
        #[serde(default = "default_tts_model")]
        tts_model: String,
        #[serde(default = "default_voice")]
        voice: String,
    }

    let legacy: Legacy = serde_json::from_value(value.clone())
        .map_err(|error| AiError(format!("not a valid AI settings document: {error}")))?;
    Ok(AiConfig {
        providers: vec![AiProvider {
            id: Uuid::new_v4().to_string(),
            name: "Provider 1".to_string(),
            priority: 1,
            base_url: legacy.base_url,
            api_key: legacy.api_key,
            chat_model: legacy.chat_model,
            tts_model: legacy.tts_model,
            voice: legacy.voice,
        }],
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn provider(priority: u32, name: &str) -> Value {
        json!({
            "name": name,
            "priority": priority,
            "base_url": "https://example.test/v1",
            "chat_model": "gpt-4o-mini",
            "tts_model": "tts-1",
            "voice": "nova",
        })
    }

    #[test]
    fn a_fresh_config_has_no_providers() {
        let config = AiConfig::default();
        assert!(config.providers.is_empty());
        assert_eq!(config.next_priority(), 1);
    }

    #[test]
    fn the_view_is_ordered_by_priority_and_never_returns_a_key() {
        let mut config = AiConfig::default();
        config
            .create(&json!({
                "name": "Second", "priority": 2, "base_url": "https://example.test/v1",
                "api_key": "sk-two", "chat_model": "gpt-4o-mini", "tts_model": "tts-1", "voice": "nova"
            }))
            .unwrap();
        config
            .create(&json!({
                "name": "First", "priority": 1, "base_url": "https://example.test/v1",
                "api_key": "sk-one", "chat_model": "gpt-4o-mini", "tts_model": "tts-1", "voice": "nova"
            }))
            .unwrap();

        let view = serde_json::to_value(config.view()).unwrap();
        let names: Vec<&str> = view["providers"]
            .as_array()
            .unwrap()
            .iter()
            .map(|provider| provider["name"].as_str().unwrap())
            .collect();
        assert_eq!(names, vec!["First", "Second"], "lowest priority first");
        assert_eq!(view["providers"][0]["api_key_set"], json!(true));
        assert!(view["providers"][0].get("api_key").is_none());
        assert_eq!(view["next_priority"], json!(3));
    }

    #[test]
    fn priority_must_be_unique() {
        let mut config = AiConfig::default();
        config.create(&provider(1, "A")).unwrap();
        let error = config.create(&provider(1, "B")).unwrap_err().to_string();
        assert!(error.contains("priority 1"), "{error}");
    }

    #[test]
    fn names_must_be_unique_even_in_a_different_case() {
        let mut config = AiConfig::default();
        config.create(&provider(1, "OpenAI")).unwrap();
        assert!(config.create(&provider(2, "openai")).is_err());
    }

    #[test]
    fn next_priority_fills_the_first_gap() {
        let mut config = AiConfig::default();
        config.create(&provider(1, "A")).unwrap();
        config.create(&provider(3, "C")).unwrap();
        assert_eq!(config.next_priority(), 2);
    }

    #[test]
    fn an_update_keeps_the_key_when_it_is_left_out() {
        let mut config = AiConfig::default();
        let created = config
            .create(&json!({
                "name": "A", "priority": 1, "base_url": "https://example.test/v1",
                "api_key": "sk-secret", "chat_model": "gpt-4o-mini", "tts_model": "tts-1", "voice": "nova"
            }))
            .unwrap();

        let updated = config.update(&created.id, &json!({ "voice": "alloy" })).unwrap();
        assert_eq!(updated.voice, "alloy");
        assert_eq!(updated.api_key, "sk-secret", "an absent key keeps the saved one");
        assert_eq!(updated.name, "A", "an absent name keeps the saved one");
    }

    #[test]
    fn an_empty_key_clears_it() {
        let mut config = AiConfig::default();
        let created = config
            .create(&json!({
                "name": "A", "priority": 1, "base_url": "https://example.test/v1",
                "api_key": "sk-secret", "chat_model": "gpt-4o-mini", "tts_model": "tts-1", "voice": "nova"
            }))
            .unwrap();
        let updated = config.update(&created.id, &json!({ "api_key": "" })).unwrap();
        assert_eq!(updated.api_key, "");
    }

    #[test]
    fn an_update_may_rename_and_reprioritise() {
        let mut config = AiConfig::default();
        let created = config.create(&provider(1, "A")).unwrap();
        let updated = config
            .update(&created.id, &json!({ "name": "B", "priority": 4 }))
            .unwrap();
        assert_eq!(updated.name, "B");
        assert_eq!(updated.priority, 4);
        // The id did not move, so the tab does not either.
        assert_eq!(updated.id, created.id);
    }

    #[test]
    fn delete_removes_the_provider() {
        let mut config = AiConfig::default();
        let created = config.create(&provider(1, "A")).unwrap();
        config.delete(&created.id).unwrap();
        assert!(config.providers.is_empty());
        assert!(config.delete(&created.id).is_err(), "deleting twice errors");
    }

    #[test]
    fn resolve_lays_the_form_over_the_stored_provider() {
        let mut config = AiConfig::default();
        let created = config
            .create(&json!({
                "name": "A", "priority": 1, "base_url": "https://example.test/v1",
                "api_key": "sk-secret", "chat_model": "gpt-4o-mini", "tts_model": "tts-1", "voice": "nova"
            }))
            .unwrap();
        let resolved = config.resolve(&created.id, &json!({ "voice": "alloy" })).unwrap();
        assert_eq!(resolved.voice, "alloy");
        assert_eq!(resolved.api_key, "sk-secret");
        // resolve skips uniqueness on purpose, so a test can run before the
        // conflicting change is saved.
        assert!(config
            .resolve(&created.id, &json!({ "priority": created.priority }))
            .is_ok());
    }

    #[test]
    fn a_draft_is_validated_but_not_stored() {
        let config = AiConfig::default();
        let draft = AiConfig::draft(&provider(1, "New")).unwrap();
        assert_eq!(draft.name, "New");
        assert!(config.providers.is_empty());
    }

    #[test]
    fn a_legacy_document_becomes_one_provider() {
        let config = AiConfig::parse(&json!({
            "base_url": "https://api.venice.ai/api/v1",
            "api_key": "k",
            "chat_model": "qwen",
            "tts_model": "tts-kokoro",
            "voice": "af_sarah"
        }))
        .unwrap();
        assert_eq!(config.providers.len(), 1);
        assert_eq!(config.providers[0].base_url, "https://api.venice.ai/api/v1");
        assert_eq!(config.providers[0].priority, 1);
        assert_eq!(config.providers[0].voice, "af_sarah");
    }

    #[test]
    fn rejects_unknown_fields_and_missing_ones() {
        assert!(AiConfig::default()
            .create(&json!({ "name": "A", "priority": 1, "nope": 1 }))
            .is_err());
        assert!(
            AiConfig::default().create(&json!({ "name": "A" })).is_err(),
            "priority is required"
        );
        assert!(AiConfig::default()
            .create(&json!({ "priority": 1 }))
            .is_err(),
            "name is required");
    }

    #[test]
    fn trims_and_requires_fields() {
        let config = AiConfig::default()
            .create(&json!({
                "name": "  A  ", "priority": 1, "base_url": "  https://example.test/v1/  ",
                "chat_model": "gpt-4o-mini", "tts_model": "tts-1", "voice": "nova"
            }))
            .unwrap();
        assert_eq!(config.name, "A");
        assert_eq!(config.base_url, "https://example.test/v1");

        assert!(AiConfig::default()
            .create(&json!({ "name": "  ", "priority": 1 }))
            .is_err());
        assert!(AiConfig::default()
            .create(&json!({ "name": "A", "priority": 1, "base_url": "api.example.test/v1" }))
            .is_err());
        assert!(AiConfig::default()
            .create(&json!({ "name": "A", "priority": 1, "tts_model": "  " }))
            .is_err());
    }
}
