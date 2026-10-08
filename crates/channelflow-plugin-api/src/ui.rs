//! What a plugin contributes to the web UI, declared in its manifest.
//!
//! These are declarations — the browser-side loader interprets them. The
//! Plugin Manager stores and serves them so the catalog page can show what a
//! plugin adds before it is ever enabled.

use serde::{Deserialize, Serialize};

/// Where a contributed `page` slots into the shell.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq, Default)]
#[serde(rename_all = "snake_case")]
pub enum UiContributionSection {
    #[default]
    Settings,
    Tools,
    Main,
}

/// One UI contribution, tagged by its `type`.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum UiContribution {
    /// A full page, e.g. a plugin's settings page.
    Page {
        id: String,
        path: String,
        title: String,
        #[serde(default)]
        icon: String,
        #[serde(default)]
        section: UiContributionSection,
        component: String,
    },
    /// A tab inside the channel editor.
    ChannelTab {
        id: String,
        title: String,
        component: String,
        #[serde(default)]
        applies_to: Vec<String>,
    },
    /// A section inside the settings page.
    SettingsSection {
        id: String,
        title: String,
        #[serde(default)]
        icon: String,
        component: String,
    },
    /// A button injected into a toolbar area.
    ToolbarAction {
        id: String,
        title: String,
        #[serde(default)]
        icon: String,
        component: String,
        #[serde(default)]
        context: String,
    },
}