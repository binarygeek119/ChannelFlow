//! The calls the server actually makes to the configured endpoint.
//!
//! Three probes answer three different questions. Listing models says the URL
//! is right and the key is accepted; a chat completion says the chat model
//! works; speaking a phrase says the TTS model and the voice work. A wrong key
//! and a wrong voice look identical from one failed request, so the test makes
//! all three and reports each.
//!
//! Kept apart from `ai.rs`, which is pure settings and stays testable without
//! a network. This is the only module that needs an HTTP client.

use std::time::{Duration, Instant};

use serde::Serialize;

use crate::ai::AiConfig;

/// Short enough to be free on a metered API and still long enough to exercise
/// the model and the voice.
const TEST_PHRASE: &str = "ChannelFlow is checking this voice.";

/// One ceiling for every probe. A local model can be slow on a cold start, so
/// this is generous; a wrong address fails long before it.
const TIMEOUT: Duration = Duration::from_secs(30);

/// One line of the test's output.
#[derive(Debug, Serialize)]
pub struct Probe {
    pub name: &'static str,
    pub ok: bool,
    pub detail: String,
}

#[derive(Debug, Serialize)]
pub struct Report {
    /// Decided by the chat and speech probes together: the page configures one
    /// chat model and one speech model, so "the AI works" means both answered.
    /// Listing models stays informational — it proves the address and the key,
    /// but a compatible server need not implement `/models` at all.
    pub ok: bool,
    pub base_url: String,
    pub chat_model: String,
    pub tts_model: String,
    pub voice: String,
    pub bytes: usize,
    pub elapsed_ms: u64,
    pub probes: Vec<Probe>,
}

/// Build the one client the server reuses, so repeated tests share a
/// connection pool and a single timeout.
pub fn client() -> reqwest::Result<reqwest::Client> {
    reqwest::Client::builder().timeout(TIMEOUT).build()
}

pub async fn run(http: &reqwest::Client, config: &AiConfig) -> Report {
    let started = Instant::now();
    let models = list_models(http, config).await;
    let chat = chat(http, config).await;
    let (speech, bytes) = speak(http, config).await;
    Report {
        ok: chat.ok && speech.ok,
        base_url: config.base_url.clone(),
        chat_model: config.chat_model.clone(),
        tts_model: config.tts_model.clone(),
        voice: config.voice.clone(),
        bytes,
        elapsed_ms: started.elapsed().as_millis() as u64,
        probes: vec![models, chat, speech],
    }
}

async fn list_models(http: &reqwest::Client, config: &AiConfig) -> Probe {
    let request = authorize(http.get(format!("{}/models", config.base_url)), config);
    match request.send().await {
        Ok(response) => {
            let status = response.status();
            let body = response.text().await.unwrap_or_default();
            if status.is_success() {
                let count = serde_json::from_str::<serde_json::Value>(&body)
                    .ok()
                    .and_then(|value| value.get("data")?.as_array().map(Vec::len));
                Probe {
                    name: "Models",
                    ok: true,
                    detail: match count {
                        Some(count) => format!("listed {count}"),
                        None => "endpoint answered".to_string(),
                    },
                }
            } else {
                Probe {
                    name: "Models",
                    ok: false,
                    detail: summarise(status, &body),
                }
            }
        }
        Err(error) => Probe {
            name: "Models",
            ok: false,
            detail: describe(&error),
        },
    }
}

/// Ask for one short reply. No `max_tokens` and no `temperature`: OpenAI's
/// newer models reject `max_tokens` and the reasoning ones reject
/// `temperature`, and the instruction keeps the answer short anyway.
///
/// `/chat/completions` has an older sibling, `/completions`, but every
/// compatible server that speaks this API speaks the chat one.
async fn chat(http: &reqwest::Client, config: &AiConfig) -> Probe {
    let failed =
        |detail: String| Probe { name: "Chat", ok: false, detail };

    let body = serde_json::json!({
        "model": config.chat_model,
        "messages": [{ "role": "user", "content": "Reply with the single word: ready" }],
    });
    let request = authorize(
        http.post(format!("{}/chat/completions", config.base_url))
            .header(reqwest::header::CONTENT_TYPE, "application/json")
            .body(body.to_string()),
        config,
    );

    let response = match request.send().await {
        Ok(response) => response,
        Err(error) => return failed(describe(&error)),
    };

    let status = response.status();
    let body = response.text().await.unwrap_or_default();
    if !status.is_success() {
        return failed(summarise(status, &body));
    }

    let value = match serde_json::from_str::<serde_json::Value>(&body) {
        Ok(value) => value,
        Err(_) => return failed("the endpoint did not answer with JSON".to_string()),
    };
    let choices = match value.get("choices").and_then(|choices| choices.as_array()) {
        Some(choices) if !choices.is_empty() => choices,
        _ => return failed("the response carried no choices".to_string()),
    };

    let reply = choices[0]
        .pointer("/message/content")
        .and_then(|content| content.as_str())
        .map(str::trim)
        .filter(|text| !text.is_empty());
    Probe {
        name: "Chat",
        ok: true,
        detail: match reply {
            Some(text) => format!("replied {}", truncate(text)),
            None => "the model answered".to_string(),
        },
    }
}

async fn speak(http: &reqwest::Client, config: &AiConfig) -> (Probe, usize) {
    let failed = |detail: String| (Probe { name: "Speech", ok: false, detail }, 0);

    let body = serde_json::json!({
        "model": config.tts_model,
        "voice": config.voice,
        "input": TEST_PHRASE,
    });
    let request = authorize(
        http.post(format!("{}/audio/speech", config.base_url))
            .header(reqwest::header::CONTENT_TYPE, "application/json")
            .body(body.to_string()),
        config,
    );

    let response = match request.send().await {
        Ok(response) => response,
        Err(error) => return failed(describe(&error)),
    };

    let status = response.status();
    if !status.is_success() {
        let body = response.text().await.unwrap_or_default();
        return failed(summarise(status, &body));
    }

    match response.bytes().await {
        Ok(bytes) if !bytes.is_empty() => {
            let detail = format!("spoke {}", size(bytes.len()));
            (
                Probe {
                    name: "Speech",
                    ok: true,
                    detail,
                },
                bytes.len(),
            )
        }
        Ok(_) => failed("the endpoint accepted the request but returned no audio".to_string()),
        Err(error) => failed(describe(&error)),
    }
}

/// Attach the bearer token only when there is one: an empty key means the
/// endpoint wants no authentication, not that it wants an empty one.
fn authorize(request: reqwest::RequestBuilder, config: &AiConfig) -> reqwest::RequestBuilder {
    if config.api_key.is_empty() {
        request
    } else {
        request.bearer_auth(&config.api_key)
    }
}

/// Turn an error response into the sentence the page shows. OpenAI-compatible
/// servers report the useful part in `error.message`; a plain-text body is
/// quoted as-is when it is short.
fn summarise(status: reqwest::StatusCode, body: &str) -> String {
    let message = serde_json::from_str::<serde_json::Value>(body)
        .ok()
        .and_then(|value| {
            value
                .pointer("/error/message")
                .or_else(|| value.pointer("/message"))
                .and_then(|message| message.as_str())
                .map(str::to_string)
        })
        .unwrap_or_else(|| {
            let trimmed = body.trim();
            if trimmed.is_empty() {
                "no message".to_string()
            } else {
                truncate(trimmed)
            }
        });
    format!("HTTP {} — {message}", status.as_u16())
}

/// The deepest cause, not reqwest's wrapper sentence. "connection refused" is
/// what the person can act on; "error sending request for url (...)" is not.
fn describe(error: &reqwest::Error) -> String {
    let mut cause: &dyn std::error::Error = error;
    while let Some(source) = cause.source() {
        cause = source;
    }
    truncate(&cause.to_string())
}

fn truncate(text: &str) -> String {
    const LIMIT: usize = 200;
    if text.chars().count() <= LIMIT {
        return text.to_string();
    }
    let head: String = text.chars().take(LIMIT).collect();
    format!("{head}…")
}

fn size(bytes: usize) -> String {
    if bytes >= 1024 {
        format!("{:.1} KiB", bytes as f64 / 1024.0)
    } else {
        format!("{bytes} B")
    }
}
