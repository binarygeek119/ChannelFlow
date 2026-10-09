//! How the core loads, lists, drives, and installs plugins.

pub mod manager;
pub mod registry;
pub mod repo;

pub use manager::PluginManager;