//! What a plugin is allowed to do, and the machine-readable names for it.
//!
//! The manifest lists permission strings, e.g. `storage:read` or
//! `network:outbound`. The base parses them up front — a plugin that asks for
//! something nobody can approve is refused at install time — and the sandbox
//! checks the parsed form when the plugin tries to act.

use serde::{Deserialize, Serialize};

/// A capability a plugin declares it needs.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Permission {
    /// Read the plugin's own namespaced storage.
    StorageRead,
    /// Write into the named storage namespace (defaults to its own).
    StorageWrite(String),
    /// Create and query the plugin's own tables in the base's Postgres.
    StorageDatabase,
    /// Make outbound HTTP requests.
    NetworkOutbound,
    /// Bind a specific inbound port.
    NetworkInbound(String),
    /// Spawn child processes.
    ProcessSpawn,
    /// Mount API routes under `/api/plugins/{id}/*`.
    ApiRoutes,
    /// Read core data such as channels.
    ApiCoreRead,
    /// Contribute full pages to the web UI.
    UiPages,
    /// Contribute settings sections to the web UI.
    UiSettings,
    /// Inject into a specific UI area.
    UiInject(String),
    /// Write files under a named area of the core's filesystem, e.g. images.
    FilesystemWrite(String),
    /// Read the core configuration.
    SystemConfigRead,
    /// Subscribe to core events.
    SystemEvents,
}

impl Permission {
    /// Turn a manifest string into a permission. Unknown strings are an error
    /// so a typo'd capability surfaces at validation, not at runtime.
    pub fn parse(raw: &str) -> Result<Permission, String> {
        let (kind, arg) = match raw.split_once(':') {
            Some((kind, arg)) => (kind, Some(arg)),
            None => (raw, None),
        };
        match (kind, arg) {
            ("storage", Some("read")) => Ok(Permission::StorageRead),
            ("storage", Some("write")) => Ok(Permission::StorageWrite(String::new())),
            ("storage", Some(arg)) if arg.starts_with("write:") => {
                Ok(Permission::StorageWrite(arg["write:".len()..].to_string()))
            }
            ("storage", Some("database")) => Ok(Permission::StorageDatabase),
            ("network", Some("outbound")) => Ok(Permission::NetworkOutbound),
            ("network", Some(arg)) if arg.starts_with("inbound:") => {
                Ok(Permission::NetworkInbound(arg["inbound:".len()..].to_string()))
            }
            ("process", Some("spawn")) => Ok(Permission::ProcessSpawn),
            ("api", Some("routes")) => Ok(Permission::ApiRoutes),
            ("api", Some("core:read")) => Ok(Permission::ApiCoreRead),
            ("ui", Some("pages")) => Ok(Permission::UiPages),
            ("ui", Some("settings")) => Ok(Permission::UiSettings),
            ("ui", Some(arg)) if arg.starts_with("inject:") => {
                Ok(Permission::UiInject(arg["inject:".len()..].to_string()))
            }
            // Any other `ui:<area>` is treated as a named injection point; a
            // media-source plugin's `ui:library_tab` is one of these.
            ("ui", Some(arg)) => Ok(Permission::UiInject(arg.to_string())),
            ("filesystem", Some(arg)) if arg.starts_with("write:") => {
                Ok(Permission::FilesystemWrite(arg["write:".len()..].to_string()))
            }
            ("system", Some("config:read")) => Ok(Permission::SystemConfigRead),
            ("system", Some("events")) => Ok(Permission::SystemEvents),
            _ => Err(format!("\"{raw}\" is not a permission the base knows")),
        }
    }

    /// The manifest string form, e.g. `storage:read`.
    pub fn as_str(&self) -> String {
        match self {
            Permission::StorageRead => "storage:read".to_string(),
            Permission::StorageWrite(namespace) => {
                if namespace.is_empty() {
                    "storage:write".to_string()
                } else {
                    format!("storage:write:{namespace}")
                }
            }
            Permission::StorageDatabase => "storage:database".to_string(),
            Permission::NetworkOutbound => "network:outbound".to_string(),
            Permission::NetworkInbound(port) => format!("network:inbound:{port}"),
            Permission::ProcessSpawn => "process:spawn".to_string(),
            Permission::ApiRoutes => "api:routes".to_string(),
            Permission::ApiCoreRead => "api:core:read".to_string(),
            Permission::UiPages => "ui:pages".to_string(),
            Permission::UiSettings => "ui:settings".to_string(),
            Permission::UiInject(area) => format!("ui:inject:{area}"),
            Permission::FilesystemWrite(area) => format!("filesystem:write:{area}"),
            Permission::SystemConfigRead => "system:config:read".to_string(),
            Permission::SystemEvents => "system:events".to_string(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_the_manifest_names() {
        for raw in [
            "storage:read",
            "storage:write",
            "storage:write:next",
            "storage:database",
            "network:outbound",
            "network:inbound:8409",
            "process:spawn",
            "api:routes",
            "api:core:read",
            "ui:pages",
            "ui:settings",
            "ui:inject:settings",
            "ui:inject:library_tab",
            "filesystem:write:images",
            "storage:write:jellyfin",
            "system:config:read",
            "system:events",
        ] {
            let parsed = Permission::parse(raw).unwrap_or_else(|e| panic!("{raw}: {e}"));
            assert_eq!(parsed.as_str(), raw);
        }
        // The shorthand `ui:<area>` is accepted and canonicalises to
        // `ui:inject:<area>`; media sources use it for `ui:library_tab`.
        assert_eq!(
            Permission::parse("ui:library_tab").unwrap().as_str(),
            "ui:inject:library_tab"
        );
    }

    #[test]
    fn rejects_unknown() {
        assert!(Permission::parse("chmod:everything").is_err());
        assert!(Permission::parse("storage:write").is_ok());
    }
}