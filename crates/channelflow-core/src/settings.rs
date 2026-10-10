//! General settings: the addresses ChannelFlow is reached at.
//!
//! `local_url` is the address on the local network. It is detected once — on
//! first boot, from the address the browser used (which is what works in
//! Docker, where the container's own IP is not the host's) falling back to the
//! primary non-loopback interface — then stored, and the operator can override
//! it in General Settings or during the walkthrough. `public_url` is entered by
//! hand and never guessed.

use std::net::UdpSocket;

use serde::{Deserialize, Serialize};

/// The General Settings page's stored values.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct GeneralSettings {
    #[serde(default)]
    pub public_url: String,
    #[serde(default)]
    pub local_url: String,
    /// IANA time zone id (e.g. `America/New_York`) the TV Guide shows times
    /// in. Empty means "the server's own zone".
    #[serde(default)]
    pub timezone: String,
    /// How many days of planned playout to build ahead (1–14; default 14).
    #[serde(default = "default_playout_days")]
    pub playout_days: i32,
    /// Seconds to keep a channel stream encoding after the last viewer leaves
    /// (0–3600; default 30).
    #[serde(default = "default_stream_idle_seconds")]
    pub stream_idle_seconds: i32,
}

fn default_playout_days() -> i32 {
    14
}

fn default_stream_idle_seconds() -> i32 {
    30
}

impl GeneralSettings {
    /// Trim and drop trailing slashes so a stored URL is canonical, and clamp
    /// the playout knobs to their valid ranges.
    pub fn normalized(mut self) -> Self {
        self.public_url = normalize(&self.public_url);
        self.local_url = normalize(&self.local_url);
        self.timezone = self.timezone.trim().to_string();
        self.playout_days = self.playout_days.clamp(1, 14);
        self.stream_idle_seconds = self.stream_idle_seconds.clamp(0, 3600);
        self
    }
}

fn normalize(url: &str) -> String {
    url.trim().trim_end_matches('/').to_string()
}

/// Whether a non-empty value is an http(s) URL we will store.
pub fn is_valid_url(url: &str) -> bool {
    let url = url.trim();
    url.is_empty()
        || url
            .get(..7)
            .is_some_and(|prefix| prefix.eq_ignore_ascii_case("http://"))
        || url
            .get(..8)
            .is_some_and(|prefix| prefix.eq_ignore_ascii_case("https://"))
}

/// Work out the local URL to suggest on first boot.
///
/// Order of preference:
/// 1. `CHANNELFLOW_LOCAL_URL` — an explicit override (the reliable Docker knob).
/// 2. The Host header of the request — what the browser actually reached, which
///    is correct behind a port mapping where the container IP is wrong.
/// 3. The primary non-loopback IPv4 plus the listening port — right for a plain
///    binary on a LAN.
pub fn detect_local_url(port: u16, host: Option<&str>) -> Option<String> {
    if let Ok(explicit) = std::env::var("CHANNELFLOW_LOCAL_URL") {
        let explicit = explicit.trim();
        if !explicit.is_empty() {
            return Some(explicit.trim_end_matches('/').to_string());
        }
    }
    if let Some(url) = host_url(host) {
        return Some(url);
    }
    primary_ipv4().map(|ip| format!("http://{ip}:{port}"))
}

/// Turn a Host header into a URL, unless it is loopback/wildcard (useless as a
/// local address).
fn host_url(host: Option<&str>) -> Option<String> {
    let host = host?.trim();
    if host.is_empty() {
        return None;
    }
    let hostname = host
        .rsplit_once(':')
        .map(|(name, _)| name)
        .unwrap_or(host)
        .trim_matches(|c| c == '[' || c == ']');
    let loopback = matches!(hostname, "localhost" | "127.0.0.1" | "::1" | "0.0.0.0" | "");
    if loopback || hostname.parse::<std::net::IpAddr>().is_ok_and(|ip| ip.is_loopback()) {
        return None;
    }
    Some(format!("http://{host}"))
}

/// The machine's primary outbound IPv4. Connecting a UDP socket sends nothing;
/// it just asks the routing table which local address would be used.
fn primary_ipv4() -> Option<std::net::IpAddr> {
    for probe in ["8.8.8.8:80", "1.1.1.1:80"] {
        if let Ok(socket) = UdpSocket::bind("0.0.0.0:0") {
            if socket.connect(probe).is_ok() {
                if let Ok(addr) = socket.local_addr() {
                    let ip = addr.ip();
                    if !ip.is_loopback() && !ip.is_unspecified() {
                        return Some(ip);
                    }
                }
            }
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn host_header_wins_over_interfaces() {
        assert_eq!(
            detect_local_url(8097, Some("192.168.1.2:8097")).as_deref(),
            Some("http://192.168.1.2:8097")
        );
        assert_eq!(
            detect_local_url(8097, Some("channelflow.lan:8097")).as_deref(),
            Some("http://channelflow.lan:8097")
        );
    }

    #[test]
    fn loopback_hosts_are_ignored() {
        assert_eq!(host_url(Some("localhost:8097")), None);
        assert_eq!(host_url(Some("127.0.0.1:8097")), None);
        assert_eq!(host_url(Some("[::1]:8097")), None);
    }

    #[test]
    fn urls_are_normalized_and_validated() {
        let s = GeneralSettings {
            public_url: " https://example.com/ ".to_string(),
            local_url: "http://192.168.1.2:8097/".to_string(),
            timezone: " America/New_York ".to_string(),
            playout_days: 30,
            stream_idle_seconds: 0,
        }
        .normalized();
        assert_eq!(s.public_url, "https://example.com");
        assert_eq!(s.local_url, "http://192.168.1.2:8097");
        assert_eq!(s.timezone, "America/New_York");
        assert_eq!(s.playout_days, 14); // clamped 1–14
        assert_eq!(s.stream_idle_seconds, 0);
        assert!(is_valid_url(""));
        assert!(is_valid_url("http://x"));
        assert!(is_valid_url("HTTPS://x"));
        assert!(!is_valid_url("ftp://x"));
    }
}
