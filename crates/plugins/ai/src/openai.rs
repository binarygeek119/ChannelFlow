//! The calls the server actually makes to a configured provider.
//!
//! Three probes answer three different questions. Listing models says the URL
//! is right and the key is accepted; a chat completion says the chat model
//! works; speaking a phrase says the TTS model and the voice work. A wrong key
//! and a wrong voice look identical from one failed request, so the test makes
//! all three and reports each.
//!
//! `failover` is the one that matters at runtime: it walks the providers in
//! priority order and stops at the first that answers, so a dead endpoint
//! costs one failed test and the next one takes over.
//!
//! Kept apart from `ai.rs`, which is pure settings and stays testable without
//! a network. This is the only module that needs an HTTP client.

use std::time::Instant;

use serde::Serialize;

use crate::ai::AiProvider;

/// Short enough to be free on a metered API and still long enough to exercise
/// the model and the voice.
const TEST_PHRASE: &str = "ChannelFlow is checking this voice.";

/// One line of the test's output.
#[derive(Debug, Serialize)]
pub struct Probe {
    pub name: &'static str,
    pub ok: bool,
    pub detail: String,
}

#[derive(Debug, Serialize)]
pub struct Report {
    /// Decided by the chat and speech probes together: a provider configures
    /// one chat model and one speech model, so "this provider works" means
    /// both answered. Listing models stays informational — it proves the
    /// address and the key, but a compatible server need not implement
    /// `/models` at all.
    pub ok: bool,
    pub name: String,
    pub priority: u32,
    pub base_url: String,
    pub chat_model: String,
    pub tts_model: String,
    pub voice: String,
    pub bytes: usize,
    pub elapsed_ms: u64,
    pub probes: Vec<Probe>,
}

/// One provider's turn in a failover test.
#[derive(Debug, Serialize)]
pub struct Attempt {
    pub name: String,
    pub priority: u32,
    pub ok: bool,
    pub detail: String,
}

#[derive(Debug, Serialize)]
pub struct FailoverReport {
    /// True when some provider answered.
    pub ok: bool,
    /// The provider the app would use: the first one, by priority, that
    /// answered. `None` when none did.
    pub chosen: Option<String>,
    /// The providers tried, in order, up to and including the one that worked.
    pub attempts: Vec<Attempt>,
}

pub async fn run(http: &reqwest::Client, provider: &AiProvider) -> Report {
    let started = Instant::now();
    let models = list_models(http, provider).await;
    let chat = chat(http, provider).await;
    let (speech, bytes) = speak(http, provider).await;
    Report {
        ok: chat.ok && speech.ok,
        name: provider.name.clone(),
        priority: provider.priority,
        base_url: provider.base_url.clone(),
        chat_model: provider.chat_model.clone(),
        tts_model: provider.tts_model.clone(),
        voice: provider.voice.clone(),
        bytes,
        elapsed_ms: started.elapsed().as_millis() as u64,
        probes: vec![models, chat, speech],
    }
}

/// Walk the providers in priority order and stop at the first that answers.
/// A provider that works costs nothing for the ones below it: they are never
/// contacted, because the app already has an endpoint to use.
pub async fn failover(http: &reqwest::Client, providers: &[&AiProvider]) -> FailoverReport {
    let mut attempts = Vec::new();
    for provider in providers {
        let report = run(http, provider).await;
        let detail = if report.ok {
            "answered — chat and speech both worked".to_string()
        } else {
            first_failure(&report)
        };
        attempts.push(Attempt {
            name: provider.name.clone(),
            priority: provider.priority,
            ok: report.ok,
            detail,
        });
        if report.ok {
            return FailoverReport {
                ok: true,
                chosen: Some(provider.name.clone()),
                attempts,
            };
        }
    }
    FailoverReport {
        ok: false,
        chosen: None,
        attempts,
    }
}

/// The first probe that did not pass, named, so the report says what broke
/// rather than only that something did.
fn first_failure(report: &Report) -> String {
    report
        .probes
        .iter()
        .find(|probe| !probe.ok)
        .map(|probe| format!("{}: {}", probe.name, probe.detail))
        .unwrap_or_else(|| "the test failed".to_string())
}

async fn list_models(http: &reqwest::Client, provider: &AiProvider) -> Probe {
    let request = authorize(http.get(format!("{}/models", provider.base_url)), provider);
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
async fn chat(http: &reqwest::Client, provider: &AiProvider) -> Probe {
    let failed =
        |detail: String| Probe { name: "Chat", ok: false, detail };

    let body = serde_json::json!({
        "model": provider.chat_model,
        "messages": [{ "role": "user", "content": "Reply with the single word: ready" }],
    });
    let request = authorize(
        http.post(format!("{}/chat/completions", provider.base_url))
            .header(reqwest::header::CONTENT_TYPE, "application/json")
            .body(body.to_string()),
        provider,
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

async fn speak(http: &reqwest::Client, provider: &AiProvider) -> (Probe, usize) {
    let failed = |detail: String| (Probe { name: "Speech", ok: false, detail }, 0);

    let body = serde_json::json!({
        "model": provider.tts_model,
        "voice": provider.voice,
        "input": TEST_PHRASE,
    });
    let request = authorize(
        http.post(format!("{}/audio/speech", provider.base_url))
            .header(reqwest::header::CONTENT_TYPE, "application/json")
            .body(body.to_string()),
        provider,
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
fn authorize(request: reqwest::RequestBuilder, provider: &AiProvider) -> reqwest::RequestBuilder {
    if provider.api_key.is_empty() {
        request
    } else {
        request.bearer_auth(&provider.api_key)
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
