//! How the core loads, lists, and drives plugins.

pub mod manager;
pub mod registry;
pub mod store_client;

pub use manager::PluginManager;
pub use registry::PluginRegistry;
pub use store_client::StoreClient;