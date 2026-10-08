//! File-backed channel storage: one JSON document per channel under
//! `<config>/channels/`.
//!
//! ChannelFlow 2.0.0 starts file-based rather than database-backed. next is
//! driven entirely by JSON documents, so keeping channels as files means the
//! on-disk state is the same shape you hand to the engine, and there is no
//! Postgres to stand up before anything works.

use std::fs;
use std::path::{Path, PathBuf};

use chrono::Utc;
use uuid::Uuid;

use crate::ai::{AiConfig, AiError};
use crate::model::{Channel, NewChannel, UpdateChannel};
use crate::transcode::{TranscodeConfig, TranscodeError};

/// Instance transcode defaults live beside `channels/`, so the whole on-disk
/// configuration of a ChannelFlow instance is one directory.
const TRANSCODE_FILE: &str = "transcode.json";

/// The AI page's connection settings. Separate from `transcode.json` because
/// it holds a secret and is written with tighter permissions.
const AI_FILE: &str = "ai.json";

/// Storage failures, kept distinct from `anyhow` so the API layer can turn
/// `NotFound` into 404, `DuplicateNumber` into 409 and `Invalid` into 400
/// instead of reporting all of them as 500.
#[derive(Debug)]
pub enum StoreError {
    NotFound(Uuid),
    DuplicateNumber(u32),
    Invalid(&'static str),
    /// A transcode document — the defaults file, or the body of a request —
    /// that next's schema would reject.
    Transcode(TranscodeError),
    /// An AI settings document that cannot be used as written.
    Ai(AiError),
    Io(std::io::Error),
    Json(serde_json::Error),
}

impl From<std::io::Error> for StoreError {
    fn from(error: std::io::Error) -> Self {
        StoreError::Io(error)
    }
}

impl From<serde_json::Error> for StoreError {
    fn from(error: serde_json::Error) -> Self {
        StoreError::Json(error)
    }
}

impl std::fmt::Display for StoreError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            StoreError::NotFound(id) => write!(f, "no channel with id {id}"),
            StoreError::DuplicateNumber(n) => write!(f, "channel number {n} is already in use"),
            StoreError::Invalid(msg) => write!(f, "{msg}"),
            StoreError::Transcode(error) => write!(f, "{error}"),
            StoreError::Ai(error) => write!(f, "{error}"),
            StoreError::Io(error) => write!(f, "storage error: {error}"),
            StoreError::Json(error) => write!(f, "channel document is not valid JSON: {error}"),
        }
    }
}

impl std::error::Error for StoreError {}

#[derive(Clone)]
pub struct Store {
    /// The config directory: `transcode.json` and, later, playout state.
    root: PathBuf,
    /// `<config>/channels`.
    dir: PathBuf,
}

impl Store {
    pub fn open(config: &Path) -> Result<Self, StoreError> {
        let dir = config.join("channels");
        fs::create_dir_all(&dir)?;
        let store = Self {
            root: config.to_path_buf(),
            dir,
        };
        store.seed_transcode()?;
        store.seed_ai()?;
        Ok(store)
    }

    /// Write `transcode.json` on first run, so the instance defaults are a
    /// real, hand-editable file from the start rather than an implied
    /// everything-unset.
    fn seed_transcode(&self) -> Result<(), StoreError> {
        let path = self.root.join(TRANSCODE_FILE);
        if !path.exists() {
            self.save_transcode_defaults(&TranscodeConfig::default())?;
            tracing::info!(path = %path.display(), "wrote default transcode settings");
        }
        Ok(())
    }

    /// The instance transcode defaults the Transcode page edits.
    pub fn transcode_defaults(&self) -> Result<TranscodeConfig, StoreError> {
        let path = self.root.join(TRANSCODE_FILE);
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(TranscodeConfig::default());
            }
            Err(error) => return Err(error.into()),
        };
        let value: serde_json::Value = serde_json::from_str(&text)?;
        TranscodeConfig::parse(&value).map_err(StoreError::Transcode)
    }

    pub fn save_transcode_defaults(&self, config: &TranscodeConfig) -> Result<(), StoreError> {
        let path = self.root.join(TRANSCODE_FILE);
        let tmp = path.with_extension("json.tmp");
        let json = serde_json::to_string_pretty(config)?;
        fs::write(&tmp, json)?;
        fs::rename(&tmp, &path)?;
        Ok(())
    }

    /// Replace one channel's transcode overrides. The caller validates the
    /// patch first; this only stores it.
    pub fn set_channel_transcode(
        &self,
        id: Uuid,
        overrides: serde_json::Value,
    ) -> Result<Channel, StoreError> {
        let mut channel = self.get(id)?;
        // `null` means "no overrides"; store the empty object so the document
        // keeps the shape the API returns and the file stays tidy.
        channel.transcode = if overrides.is_null() {
            serde_json::Value::Object(serde_json::Map::new())
        } else {
            overrides
        };
        channel.updated_at = Utc::now();
        self.write(&channel)?;
        Ok(channel)
    }

    /// Write `ai.json` on first run for the same reason as `transcode.json`:
    /// the page starts from a real file naming the OpenAI defaults.
    fn seed_ai(&self) -> Result<(), StoreError> {
        let path = self.root.join(AI_FILE);
        if !path.exists() {
            self.save_ai_config(&AiConfig::default())?;
            tracing::info!(path = %path.display(), "wrote default AI settings");
        }
        Ok(())
    }

    /// The AI connection settings the AI page edits.
    pub fn ai_config(&self) -> Result<AiConfig, StoreError> {
        let path = self.root.join(AI_FILE);
        let text = match fs::read_to_string(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(AiConfig::default());
            }
            Err(error) => return Err(error.into()),
        };
        let value: serde_json::Value = serde_json::from_str(&text)?;
        AiConfig::parse(&value).map_err(StoreError::Ai)
    }

    /// Written `0600`: this is the one file that holds a secret, so it is not
    /// left readable by every account on the host even when the config
    /// directory is.
    pub fn save_ai_config(&self, config: &AiConfig) -> Result<(), StoreError> {
        let path = self.root.join(AI_FILE);
        let json = serde_json::to_string_pretty(config)?;
        write_private(&path, &json)?;
        Ok(())
    }

    fn path(&self, id: Uuid) -> PathBuf {
        self.dir.join(format!("{id}.json"))
    }

    /// Every channel, ordered by number then name.
    pub fn list(&self) -> Result<Vec<Channel>, StoreError> {
        let mut channels = Vec::new();
        for entry in fs::read_dir(&self.dir)? {
            let entry = entry?;
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("json") {
                continue;
            }
            let text = fs::read_to_string(&path)?;
            match serde_json::from_str::<Channel>(&text) {
                Ok(channel) => channels.push(channel),
                Err(error) => {
                    tracing::warn!(path = %path.display(), %error, "skipping unreadable channel");
                }
            }
        }
        channels.sort_by(|a, b| a.number.cmp(&b.number).then_with(|| a.name.cmp(&b.name)));
        Ok(channels)
    }

    pub fn get(&self, id: Uuid) -> Result<Channel, StoreError> {
        let text = fs::read_to_string(self.path(id)).map_err(|_| StoreError::NotFound(id))?;
        serde_json::from_str(&text).map_err(|_| StoreError::NotFound(id))
    }

    pub fn create(&self, input: NewChannel) -> Result<Channel, StoreError> {
        let name = input.name.trim();
        if name.is_empty() {
            return Err(StoreError::Invalid("channel name cannot be empty"));
        }
        if input.number == 0 {
            return Err(StoreError::Invalid("channel number must be at least 1"));
        }
        self.reserve(input.number, None)?;

        let now = Utc::now();
        let channel = Channel {
            id: Uuid::new_v4(),
            number: input.number,
            name: name.to_string(),
            description: input.description.trim().to_string(),
            enabled: input.enabled,
            transcode: serde_json::Value::Object(serde_json::Map::new()),
            created_at: now,
            updated_at: now,
        };

        self.write(&channel)?;
        Ok(channel)
    }

    pub fn update(&self, id: Uuid, input: UpdateChannel) -> Result<Channel, StoreError> {
        let mut channel = self.get(id)?;

        if let Some(number) = input.number {
            if number == 0 {
                return Err(StoreError::Invalid("channel number must be at least 1"));
            }
            self.reserve(number, Some(id))?;
            channel.number = number;
        }
        if let Some(name) = input.name {
            let name = name.trim();
            if name.is_empty() {
                return Err(StoreError::Invalid("channel name cannot be empty"));
            }
            channel.name = name.to_string();
        }
        if let Some(description) = input.description {
            channel.description = description.trim().to_string();
        }
        if let Some(enabled) = input.enabled {
            channel.enabled = enabled;
        }
        channel.updated_at = Utc::now();

        self.write(&channel)?;
        Ok(channel)
    }

    pub fn delete(&self, id: Uuid) -> Result<(), StoreError> {
        fs::remove_file(self.path(id)).map_err(|_| StoreError::NotFound(id))
    }

    /// Create the first channel so a fresh install has something to look at.
    /// Only ever runs against an empty store.
    pub fn seed(&self) -> Result<Option<Channel>, StoreError> {
        if !self.list()?.is_empty() {
            return Ok(None);
        }
        let channel = self.create(NewChannel {
            number: 1,
            name: "ChannelFlow One".to_string(),
            description: "First channel — edit or replace me".to_string(),
            enabled: true,
        })?;
        tracing::info!(number = channel.number, name = %channel.name, "seeded first channel");
        Ok(Some(channel))
    }

    /// Reject a channel number that another channel already owns.
    fn reserve(&self, number: u32, ignore: Option<Uuid>) -> Result<(), StoreError> {
        for existing in self.list()? {
            if Some(existing.id) == ignore {
                continue;
            }
            if existing.number == number {
                return Err(StoreError::DuplicateNumber(number));
            }
        }
        Ok(())
    }

    fn write(&self, channel: &Channel) -> Result<(), StoreError> {
        let path = self.path(channel.id);
        let tmp = path.with_extension("json.tmp");
        let json = serde_json::to_string_pretty(channel)?;
        fs::write(&tmp, json)?;
        fs::rename(&tmp, &path)?;
        Ok(())
    }
}

/// Write a file that holds a secret. The temporary file is created with mode
/// `0600` rather than written and then chmodded, so the key is never on disk
/// world-readable for even the instant between the two.
fn write_private(path: &Path, contents: &str) -> std::io::Result<()> {
    use std::io::Write;

    let tmp = path.with_extension("json.tmp");
    let mut options = fs::OpenOptions::new();
    options.write(true).create(true).truncate(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let mut file = options.open(&tmp)?;
    file.write_all(contents.as_bytes())?;
    drop(file);
    fs::rename(&tmp, path)
}
