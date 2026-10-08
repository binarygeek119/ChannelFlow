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

use crate::model::{Channel, NewChannel, UpdateChannel};

/// Storage failures, kept distinct from `anyhow` so the API layer can turn
/// `NotFound` into 404, `DuplicateNumber` into 409 and `Invalid` into 400
/// instead of reporting all of them as 500.
#[derive(Debug)]
pub enum StoreError {
    NotFound(Uuid),
    DuplicateNumber(u32),
    Invalid(&'static str),
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
            StoreError::Io(error) => write!(f, "storage error: {error}"),
            StoreError::Json(error) => write!(f, "channel document is not valid JSON: {error}"),
        }
    }
}

impl std::error::Error for StoreError {}

#[derive(Clone)]
pub struct Store {
    dir: PathBuf,
}

impl Store {
    pub fn open(config: &Path) -> Result<Self, StoreError> {
        let dir = config.join("channels");
        fs::create_dir_all(&dir)?;
        Ok(Self { dir })
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
