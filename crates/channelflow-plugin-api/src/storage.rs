//! Access to a plugin's own, namespaced key/value storage.
//!
//! The core backs this over its two backends (files or Postgres) and scopes it
//! strictly to the plugin's id, so two plugins can never see each other's
//! settings and no plugin can touch core documents through it.

use async_trait::async_trait;
use serde::{Deserialize, Serialize};
use serde_json::Value;

/// A storage round trip failed.
#[derive(Debug, Clone)]
pub struct PluginStorageError(pub String);

impl std::fmt::Display for PluginStorageError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for PluginStorageError {}

/// A key/value store scoped to one plugin. Values are JSON documents.
#[async_trait]
pub trait PluginStorage: Send + Sync {
    async fn get(&self, key: &str) -> Result<Option<Value>, PluginStorageError>;
    async fn set(&self, key: &str, value: &Value) -> Result<(), PluginStorageError>;
    async fn delete(&self, key: &str) -> Result<(), PluginStorageError>;
}

/// A reading test double.
#[derive(Debug, Default)]
pub struct InMemoryStorage {
    inner: std::collections::HashMap<String, Value>,
}

impl InMemoryStorage {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn seed(&mut self, key: &str, value: Value) {
        self.inner.insert(key.to_string(), value);
    }
}

#[async_trait]
impl PluginStorage for InMemoryStorage {
    async fn get(&self, key: &str) -> Result<Option<Value>, PluginStorageError> {
        Ok(self.inner.get(key).cloned())
    }

    async fn set(&self, key: &str, value: &Value) -> Result<(), PluginStorageError> {
        let mut inner = self.inner.clone();
        inner.insert(key.to_string(), value.clone());
        Ok(())
    }

    async fn delete(&self, key: &str) -> Result<(), PluginStorageError> {
        let mut inner = self.inner.clone();
        inner.remove(key);
        Ok(())
    }
}

/// A value that survives the round trip, for tests that pass through
/// `set` then `get`.
#[derive(Debug, Serialize, Deserialize)]
pub struct RoundTrip {
    pub key: String,
    pub value: Value,
}