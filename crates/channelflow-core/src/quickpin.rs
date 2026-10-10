//! Quick Pin pairing.
//!
//! An app shows an 8-character PIN; the operator types it on the Quick Pin
//! page. This instance encrypts its Live TV URLs (local and public) with a key
//! derived from that PIN and hands the ciphertext to the relay
//! (`channelflow-pin-server`), which forwards it to the waiting app. The relay
//! never sees the key or the URLs, and this process never opens the ciphertext
//! again. See the relay's `PROTOCOL.md`.

use aes_gcm::aead::{Aead, KeyInit};
use aes_gcm::{Aes256Gcm, Nonce};
use base64::engine::general_purpose::STANDARD;
use base64::Engine as _;
use sha2::{Digest, Sha256};

use crate::settings::GeneralSettings;

/// The default relay origin (the GCP/DuckDNS deployment).
pub const DEFAULT_SERVER: &str = "https://channelflow.duckdns.org";

const KEY_CONTEXT: &[u8] = b"ChannelFlow QuickPin v1";

/// Uppercase and drop everything that is not a letter or digit, so a displayed
/// `K7M2-Q9AB` and a typed `k7m2 q9ab` are the same PIN.
pub fn normalize_pin(pin: &str) -> String {
    pin.chars()
        .filter(|c| c.is_ascii_alphanumeric())
        .map(|c| c.to_ascii_uppercase())
        .collect()
}

pub fn is_valid_pin(pin: &str) -> bool {
    let pin = normalize_pin(pin);
    pin.len() == 8
}

/// `SHA256("ChannelFlow QuickPin v1" || ASCII(normalized PIN))`.
fn key(pin: &str) -> [u8; 32] {
    let mut hasher = Sha256::new();
    hasher.update(KEY_CONTEXT);
    hasher.update(normalize_pin(pin).as_bytes());
    hasher.finalize().into()
}

/// AES-256-GCM, `nonce(12) || ciphertext || tag(16)`, standard Base64.
pub fn encrypt(pin: &str, plaintext: &[u8]) -> Result<String, String> {
    let cipher = Aes256Gcm::new_from_slice(&key(pin)).map_err(|error| error.to_string())?;
    let mut nonce = [0u8; 12];
    nonce.copy_from_slice(&uuid::Uuid::new_v4().as_bytes()[..12]);
    let ciphertext = cipher
        .encrypt(Nonce::from_slice(&nonce), plaintext)
        .map_err(|error| error.to_string())?;
    let mut blob = Vec::with_capacity(12 + ciphertext.len());
    blob.extend_from_slice(&nonce);
    blob.extend_from_slice(&ciphertext);
    Ok(STANDARD.encode(blob))
}

fn base_url(value: &str) -> Option<String> {
    let trimmed = value.trim().trim_end_matches('/');
    (!trimmed.is_empty()).then(|| trimmed.to_string())
}

/// The six-key JSON the app decrypts. `primary_local` puts the local pair in
/// `m3u`/`xmltv`; the public pair goes there otherwise. `api_key` is appended
/// as `?apiKey=` when set, so the app's player authenticates with its own key.
pub fn payload(settings: &GeneralSettings, primary_local: bool, api_key: &str) -> serde_json::Value {
    let local = base_url(&settings.local_url);
    let public = base_url(&settings.public_url);
    let keyed = |base: &str| {
        let url = format!("{base}/live/channels.m3u");
        let xmltv = format!("{base}/live/xmltv.xml");
        (append_key(url, api_key), append_key(xmltv, api_key))
    };
    let local_urls = local.as_deref().map(keyed);
    let public_urls = public.as_deref().map(keyed);
    let (local_m3u, local_xmltv) = match &local_urls {
        Some((m3u, xmltv)) => (Some(m3u.clone()), Some(xmltv.clone())),
        None => (None, None),
    };
    let (public_m3u, public_xmltv) = match &public_urls {
        Some((m3u, xmltv)) => (Some(m3u.clone()), Some(xmltv.clone())),
        None => (None, None),
    };
    let (m3u, xmltv) = if primary_local {
        (
            local_m3u.clone().or_else(|| public_m3u.clone()),
            local_xmltv.clone().or_else(|| public_xmltv.clone()),
        )
    } else {
        (
            public_m3u.clone().or_else(|| local_m3u.clone()),
            public_xmltv.clone().or_else(|| local_xmltv.clone()),
        )
    };
    serde_json::json!({
        "m3u": m3u.unwrap_or_default(),
        "xmltv": xmltv.unwrap_or_default(),
        "m3uPublic": public_m3u.unwrap_or_default(),
        "xmltvPublic": public_xmltv.unwrap_or_default(),
        "m3uLocal": local_m3u.unwrap_or_default(),
        "xmltvLocal": local_xmltv.unwrap_or_default(),
    })
}

/// Append `?apiKey=<key>` (or `&`) when a key is set. Keys are hex, so no
/// escaping is needed.
pub fn append_key(mut url: String, api_key: &str) -> String {
    let api_key = api_key.trim();
    if api_key.is_empty() {
        return url;
    }
    let separator = if url.contains('?') { '&' } else { '?' };
    url.push(separator);
    url.push_str("apiKey=");
    url.push_str(api_key);
    url
}

/// The host part of a URL or Host header, lowercased, without scheme/port.
fn host_of(value: &str) -> Option<String> {
    let rest = value.split_once("://").map(|(_, rest)| rest).unwrap_or(value);
    let authority = rest.split(['/', '?', '#']).next().unwrap_or("");
    let hostport = authority.rsplit_once('@').map(|(_, h)| h).unwrap_or(authority);
    let host = hostport.rsplit_once(':').map(|(h, _)| h).unwrap_or(hostport);
    let host = host.trim_matches(|c| c == '[' || c == ']').to_ascii_lowercase();
    (!host.is_empty()).then_some(host)
}

/// Pair from the LAN (primary = local) when the admin reached this instance at
/// a loopback address or at the local URL's own host; otherwise primary =
/// public.
pub fn primary_is_local(settings: &GeneralSettings, request_host: Option<&str>) -> bool {
    let Some(host) = request_host.and_then(host_of) else {
        return true;
    };
    if matches!(host.as_str(), "localhost" | "127.0.0.1" | "::1" | "0.0.0.0") {
        return true;
    }
    host_of(&settings.local_url).is_some_and(|local| local == host)
}

/// Hand the ciphertext to the relay. `true` when an app was waiting (`204`).
pub async fn deliver(
    http: &reqwest::Client,
    server: &str,
    pin: &str,
    ciphertext: &str,
) -> Result<bool, String> {
    let origin = server.trim().trim_end_matches('/');
    if origin.is_empty() {
        return Err("no pin server is configured".to_string());
    }
    let pin = normalize_pin(pin);
    let url = format!("{origin}/v1/pins/{pin}/deliver");
    let response = http
        .post(&url)
        .json(&serde_json::json!({ "ciphertext": ciphertext }))
        .send()
        .await
        .map_err(|error| format!("could not reach the pin server: {error}"))?;
    match response.status().as_u16() {
        204 => Ok(true),
        404 => Ok(false),
        other => Err(format!("the pin server answered HTTP {other}")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pins_normalize_and_validate() {
        assert_eq!(normalize_pin(" k7m2-q9ab "), "K7M2Q9AB");
        assert_eq!(normalize_pin("K7M2Q9AB"), "K7M2Q9AB");
        assert!(is_valid_pin("k7m2-q9ab"));
        assert!(!is_valid_pin("abc"));
        assert!(!is_valid_pin("K7M2Q9ABC"));
    }

    #[test]
    fn key_is_stable_and_encryption_round_trips() {
        assert_eq!(key("K7M2-Q9AB"), key("k7m2q9ab"));
        let plain = b"{\"m3u\":\"http://x/live/channels.m3u\"}";
        let blob = encrypt("K7M2Q9AB", plain).unwrap();
        let raw = STANDARD.decode(blob).unwrap();
        assert!(raw.len() > 12 + 16);
        // Decrypt with the same PIN-derived key to prove the shape.
        let cipher = Aes256Gcm::new_from_slice(&key("K7M2Q9AB")).unwrap();
        let out = cipher
            .decrypt(Nonce::from_slice(&raw[..12]), &raw[12..])
            .unwrap();
        assert_eq!(out, plain);
    }

    #[test]
    fn payload_prefers_the_requested_pair() {
        let settings = GeneralSettings {
            local_url: "http://192.168.1.7:8097".to_string(),
            public_url: "https://cf.example.com".to_string(),
            timezone: String::new(),
        };
        let local = payload(&settings, true, "");
        assert_eq!(local["m3u"], "http://192.168.1.7:8097/live/channels.m3u");
        assert_eq!(local["m3uPublic"], "https://cf.example.com/live/channels.m3u");
        let public = payload(&settings, false, "");
        assert_eq!(public["m3u"], "https://cf.example.com/live/channels.m3u");
        assert_eq!(public["xmltvLocal"], "http://192.168.1.7:8097/live/xmltv.xml");
    }

    #[test]
    fn payload_appends_the_api_key() {
        let settings = GeneralSettings {
            local_url: "http://192.168.1.7:8097".to_string(),
            public_url: "".to_string(),
            timezone: String::new(),
        };
        let keyed = payload(&settings, true, "abc123");
        assert_eq!(keyed["m3u"], "http://192.168.1.7:8097/live/channels.m3u?apiKey=abc123");
        assert_eq!(keyed["xmltvLocal"], "http://192.168.1.7:8097/live/xmltv.xml?apiKey=abc123");
    }

    #[test]
    fn primary_is_local_for_lan_hosts() {
        let settings = GeneralSettings {
            local_url: "http://192.168.1.7:8097".to_string(),
            public_url: "https://cf.example.com".to_string(),
            timezone: String::new(),
        };
        assert!(primary_is_local(&settings, Some("192.168.1.7:8097")));
        assert!(primary_is_local(&settings, Some("localhost:8097")));
        assert!(!primary_is_local(&settings, Some("cf.example.com")));
    }
}
