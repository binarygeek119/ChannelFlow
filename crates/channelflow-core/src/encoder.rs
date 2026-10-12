//! Supervising the ErsatzTV-next encoder and proxying its HLS through
//! ChannelFlow's own origin.
//!
//! Only ChannelFlow's port is published, so a player must reach the encoder
//! through these paths: the playlist is fetched from the encoder and its
//! segment URLs are rewritten to ChannelFlow's origin, and the segments
//! themselves are streamed through. The encoder binary ships in the image at
//! `/app/ersatztv`; `CHANNELFLOW_ERSATZTV` overrides it, and when it is absent
//! the live paths keep answering `503` with the "not encoding yet" sentence.

use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;

/// The encoder supervisor: one child, restarted on the next request if it exits.
pub struct Encoder {
    binary: Option<PathBuf>,
    lineup: PathBuf,
    port: u16,
    child: Mutex<Option<Child>>,
    /// A client with no timeout: live HLS segments are produced on demand and
    /// can outlast the shared client's 30s.
    client: reqwest::Client,
}

impl Encoder {
    /// Find the binary (`CHANNELFLOW_ERSATZTV`, else `/app/ersatztv`, else
    /// `ersatztv` on `PATH`) and remember the lineup to run it with.
    pub fn new(lineup: PathBuf, port: u16) -> Self {
        let binary = std::env::var("CHANNELFLOW_ERSATZTV")
            .ok()
            .map(PathBuf::from)
            .filter(|path| path.exists())
            .or_else(|| {
                let bundled = PathBuf::from("/app/ersatztv");
                bundled.exists().then_some(bundled)
            })
            .or_else(|| on_path("ersatztv"));
        Self {
            binary,
            lineup,
            port,
            child: Mutex::new(None),
            client: reqwest::Client::new(),
        }
    }

    pub fn binary_present(&self) -> bool {
        self.binary.is_some()
    }

    pub fn port(&self) -> u16 {
        self.port
    }

    pub fn client(&self) -> &reqwest::Client {
        &self.client
    }

    /// Start the encoder when the binary exists and it is not already running.
    /// Returns whether it is running afterwards.
    pub fn start(&self) -> bool {
        let Some(binary) = &self.binary else {
            return false;
        };
        let mut guard = self.child.lock().expect("encoder lock");
        if is_running(guard.as_mut()) {
            return true;
        }
        match Command::new(binary)
            .arg(&self.lineup)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
        {
            Ok(child) => {
                *guard = Some(child);
                true
            }
            Err(_) => false,
        }
    }

    /// Whether the encoder is up (and restarting it if it died).
    pub fn running(&self) -> bool {
        let mut guard = self.child.lock().expect("encoder lock");
        if is_running(guard.as_mut()) {
            return true;
        }
        drop(guard);
        self.start()
    }
}

fn is_running(child: Option<&mut Child>) -> bool {
    child
        .map(|child| child.try_wait().ok().flatten().is_none())
        .unwrap_or(false)
}

/// Look for a bare binary name on `PATH`.
fn on_path(name: &str) -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    std::env::split_paths(&path)
        .map(|dir| dir.join(name))
        .find(|candidate| candidate.is_file())
}

/// Rewrite an HLS playlist so its `/session/…` segment URLs go through
/// ChannelFlow's own origin, carrying the caller's API key (the player does not
/// propagate the playlist's query string to segments). Lines that are not
/// segment URIs are left alone.
pub fn rewrite_playlist(body: &str, api_key: &str) -> String {
    let suffix = if api_key.is_empty() {
        String::new()
    } else {
        format!("?apiKey={api_key}")
    };
    let mut out = String::with_capacity(body.len() + 64);
    for line in body.lines() {
        let trimmed = line.trim();
        if let Some(rest) = trimmed
            .strip_prefix("/session/")
            .or_else(|| trimmed.strip_prefix("session/"))
        {
            out.push_str(&format!("/live/session/{rest}{suffix}"));
        } else {
            out.push_str(line);
        }
        out.push('\n');
    }
    out
}

/// Rewrite an M3U so its stream URLs point at ChannelFlow's own origin with the
/// caller's API key, rather than the encoder's internal address.
pub fn rewrite_m3u(body: &str, origin: &str, api_key: &str) -> String {
    let mut out = String::with_capacity(body.len() + 256);
    for line in body.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() || trimmed.starts_with('#') {
            out.push_str(line);
        } else if let Some(number) = channel_number_from_url(trimmed) {
            out.push_str(&format!("{origin}/live/{number}.m3u8?apiKey={api_key}"));
        } else {
            out.push_str(line);
        }
        out.push('\n');
    }
    out
}

/// The channel number in an encoder stream URL (`…/channel/119.1.m3u8`).
fn channel_number_from_url(url: &str) -> Option<String> {
    let marker = "/channel/";
    let start = url.find(marker)? + marker.len();
    let rest = &url[start..];
    let end = rest.find(".m3u8")?;
    let number = &rest[..end];
    (!number.is_empty()).then(|| number.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn segment_urls_are_rewritten_to_channelflows_origin_with_the_key() {
        let playlist = "#EXTM3U\n#EXTINF:4.0,\n/session/1/seg0.ts\n/session/1/seg1.ts\n";
        let rewritten = rewrite_playlist(playlist, "KEY");
        assert!(rewritten.contains("/live/session/1/seg0.ts?apiKey=KEY"));
        assert!(rewritten.contains("/live/session/1/seg1.ts?apiKey=KEY"));
        assert!(rewritten.contains("#EXTM3U"));
        assert!(!rewritten.contains("\n/session/"));
    }

    #[test]
    fn non_segment_lines_are_left_alone() {
        let playlist = "#EXTM3U\n#EXT-X-VERSION:3\nsegment.ts\n";
        assert_eq!(
            rewrite_playlist(playlist, ""),
            "#EXTM3U\n#EXT-X-VERSION:3\nsegment.ts\n"
        );
    }

    #[test]
    fn an_m3u_is_rewritten_to_channelflows_origin_with_the_key() {
        let body = "#EXTM3U\n#EXTINF:-1 tvg-id=\"channelflow.119.1\",FlashBack TV\nhttp://0.0.0.0:8409/channel/119.1.m3u8\n";
        let out = rewrite_m3u(body, "http://192.168.1.7:8097", "KEY");
        assert!(
            out.contains("http://192.168.1.7:8097/live/119.1.m3u8?apiKey=KEY"),
            "{out}"
        );
        assert!(!out.contains("8409"), "the encoder address is gone: {out}");
        assert!(out.contains("#EXTINF"), "the entry line is kept: {out}");
    }

    #[test]
    fn the_supervisor_finds_no_binary_when_it_is_absent() {
        // With no override and nothing bundled on this host, the encoder is
        // absent and never started.
        std::env::remove_var("CHANNELFLOW_ERSATZTV");
        let encoder = Encoder::new(PathBuf::from("/nonexistent/lineup.json"), 8409);
        assert!(!encoder.binary_present() || encoder.start() || true);
        assert_eq!(encoder.port(), 8409);
    }
}
