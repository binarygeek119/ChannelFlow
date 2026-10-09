//! A plugin's own tables in the base's Postgres.
//!
//! Gated by the `storage:database` permission. Every table a plugin creates is
//! prefixed with its id, so `create_table("audit", …)` becomes
//! `cf_com_channelflow_ai_audit` — no namespace can collide with the core's
//! tables or another plugin's. Callers format their own SQL; the permission
//! is the trust boundary, the same way filesystem permissions are for the
//! file backend.
//!
//! On the file backend there is no database: [`PluginDatabase::table_of`]
//! returns `None` and the other calls error, which is how a plugin notices it
//! should fall back to its key/value storage.

use async_trait::async_trait;

/// A database call failed.
#[derive(Debug, Clone)]
pub struct PluginDatabaseError(pub String);

impl std::fmt::Display for PluginDatabaseError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for PluginDatabaseError {}

/// The plugin's slice of the base's Postgres.
#[async_trait]
pub trait PluginDatabase: Send + Sync {
    /// The fully-prefixed name this plugin's `name` lives under, or `None`
    /// when there is no database (the file backend).
    fn table_of(&self, name: &str) -> Option<String>;

    /// Create a table owned by this plugin if it does not already exist.
    ///
    /// `name` must be a plain identifier; `columns` is the SQL between the
    /// parentheses, e.g. `"id BIGSERIAL PRIMARY KEY, note TEXT"`.
    async fn create_table(&self, name: &str, columns: &str) -> Result<(), PluginDatabaseError>;

    /// Run one statement — DDL or DML — returning rows affected (0 for DDL).
    async fn execute(&self, sql: &str) -> Result<u64, PluginDatabaseError>;

    /// Like [`Self::execute`], with `$1..$n` parameters. Values are bound as
    /// text; cast in the SQL where a typed column needs it (`$1::int`).
    async fn execute_params(
        &self,
        sql: &str,
        params: &[serde_json::Value],
    ) -> Result<u64, PluginDatabaseError>;

    /// Run a query and return its rows as JSON objects. The query is wrapped
    /// in `json_agg(row_to_json(...))`, so any `SELECT` works.
    async fn fetch(&self, sql: &str) -> Result<Vec<serde_json::Value>, PluginDatabaseError>;

    /// Like [`Self::fetch`] with `$1..$n` parameters, bound as text.
    async fn fetch_params(
        &self,
        sql: &str,
        params: &[serde_json::Value],
    ) -> Result<Vec<serde_json::Value>, PluginDatabaseError>;
}

/// The double used on the file backend and in tests: there is no database.
#[derive(Debug, Default)]
pub struct NoPluginDatabase;

#[async_trait]
impl PluginDatabase for NoPluginDatabase {
    fn table_of(&self, _name: &str) -> Option<String> {
        None
    }

    async fn create_table(&self, _name: &str, _columns: &str) -> Result<(), PluginDatabaseError> {
        Err(PluginDatabaseError(
            "this backend has no database — set DATABASE_URL".to_string(),
        ))
    }

    async fn execute(&self, _sql: &str) -> Result<u64, PluginDatabaseError> {
        Err(PluginDatabaseError(
            "this backend has no database — set DATABASE_URL".to_string(),
        ))
    }

    async fn execute_params(
        &self,
        _sql: &str,
        _params: &[serde_json::Value],
    ) -> Result<u64, PluginDatabaseError> {
        Err(PluginDatabaseError(
            "this backend has no database — set DATABASE_URL".to_string(),
        ))
    }

    async fn fetch(&self, _sql: &str) -> Result<Vec<serde_json::Value>, PluginDatabaseError> {
        Err(PluginDatabaseError(
            "this backend has no database — set DATABASE_URL".to_string(),
        ))
    }

    async fn fetch_params(
        &self,
        _sql: &str,
        _params: &[serde_json::Value],
    ) -> Result<Vec<serde_json::Value>, PluginDatabaseError> {
        Err(PluginDatabaseError(
            "this backend has no database — set DATABASE_URL".to_string(),
        ))
    }
}