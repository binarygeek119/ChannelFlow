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

/// What the core hands a plugin that asked for `api:core:read`.
#[async_trait]
pub trait CoreData: Send + Sync {
    /// Every channel, ordered by number then name.
    async fn channels(&self) -> Result<Vec<CoreChannel>, CoreDataError>;
    /// The ids of the plugins installed in the registry (e.g. to decide which
    /// contributed presets to show).
    async fn installed_plugins(&self) -> Result<Vec<String>, CoreDataError>;
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