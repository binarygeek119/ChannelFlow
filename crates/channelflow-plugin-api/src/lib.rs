//! The ChannelFlow plugin SDK: the contract a plugin is written against.
//!
//! A plugin is a crate that depends on this SDK, describes itself in a
//! `plugin.json` manifest, and implements the [`Plugin`] trait. The core loads
//! it, gives it a [`PluginApi`] (namespaced storage, an HTTP client, the base
//! version), and drives its lifecycle — load, enable, disable, unload — in
//! that order. Routes the plugin returns through [`Plugin::routes`] are
//! mounted by the core under `/api/plugins/{id}`.
//!
//! Today the plugins shipped with the base are compiled in; the same trait is
//! what a dynamic loader will call once plugins load from disk. The ABI bump
//! exists for that moment: [`PLUGIN_ABI_VERSION`] must match between core and
//! plugin, and is checked before a plugin is allowed to run.

pub mod core;
pub mod database;
pub mod manifest;
pub mod media;
pub mod permission;
pub mod plugin;
pub mod storage;
pub mod ui;
pub mod version;

pub use core::{CoreChannel, CoreData, CoreDataError, InMemoryCoreData, NoCoreData};
pub use database::{NoPluginDatabase, PluginDatabase, PluginDatabaseError};
pub use manifest::{Assets, Hooks, PluginManifest, Repository};
pub use media::{
    Connection, FieldSpec, Library, MediaSource, MediaType, SyncCtx, SyncReport, TestCode,
    TestResult,
};
pub use permission::Permission;
pub use plugin::{
    NoopPlugin, Plugin, PluginApi, PluginError, PluginHealth, PluginLogger, PLUGIN_ABI_VERSION,
};
pub use storage::{PluginStorage, PluginStorageError};
pub use ui::{UiContribution, UiContributionSection};
pub use version::compatible;