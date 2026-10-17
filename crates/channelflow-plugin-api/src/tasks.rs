//! Background work a plugin wants the base to show as running.
//!
//! A plugin that starts something long — a horizon build, a guide refresh, a
//! newscast render, a provider failover test — reports it through the
//! [`TaskHost`] it was handed in its [`PluginApi`](crate::PluginApi). The base
//! holds the reports in memory and serves them from `GET /api/tasks/running`,
//! so the web UI's status popup can show the work from any page and bring it
//! back after the browser reloads.
//!
//! Reporting is best-effort and entirely optional: a plugin that never reports
//! simply never appears, and the base drops every task a plugin left behind
//! when that plugin unloads.

use std::sync::Arc;

/// One background task, as the base's status popup shows it.
#[derive(Debug, Clone, Default)]
pub struct TaskInfo {
    /// Stable id, local to the plugin (for example `build-horizon`). The base
    /// prefixes it with the plugin's manifest id, so two plugins can both use
    /// `build` without colliding.
    pub key: String,
    /// The popup's heading, for example "Scheduler build".
    pub title: String,
    /// The live detail line under the heading, for example
    /// "WXYZ 7 · 2026-10-16".
    pub label: Option<String>,
    /// Counted progress, shown as "current of total".
    pub current: Option<u64>,
    pub total: Option<u64>,
}

/// Where a plugin reports its own background work. The base implements this;
/// the reports surface on `GET /api/tasks/running` and in the web UI's status
/// popup.
pub trait TaskHost: Send + Sync {
    /// Report a task as running. Reporting the same key again updates it in
    /// place, so a caller may use this instead of [`TaskHost::task_progress`].
    fn task_started(&self, task: TaskInfo);

    /// Replace a running task's detail line, and its counts when given.
    fn task_progress(&self, key: &str, label: &str, current: Option<u64>, total: Option<u64>);

    /// The task ended — done, failed or cancelled — and leaves the popup.
    fn task_finished(&self, key: &str);
}

/// A running task that clears itself when dropped, so a task cannot be left
/// showing because its work returned early, panicked, or had its HTTP request
/// abandoned. Hold it for as long as the work runs.
pub struct TaskGuard {
    host: Arc<dyn TaskHost>,
    key: String,
}

impl TaskGuard {
    /// Report `task` as running and take responsibility for clearing it.
    pub fn start(host: Arc<dyn TaskHost>, task: TaskInfo) -> Self {
        let key = task.key.clone();
        host.task_started(task);
        Self { host, key }
    }

    /// Update the detail line (and the counts, when given).
    pub fn progress(&self, label: &str, current: Option<u64>, total: Option<u64>) {
        self.host.task_progress(&self.key, label, current, total);
    }

    /// The key this guard reported, including the base's plugin prefix.
    pub fn key(&self) -> &str {
        &self.key
    }
}

impl Drop for TaskGuard {
    fn drop(&mut self) {
        self.host.task_finished(&self.key);
    }
}

/// A [`TaskHost`] that drops every report, for tests and for a base with no
/// status surface.
#[derive(Debug, Default, Clone, Copy)]
pub struct NoopTaskHost;

impl TaskHost for NoopTaskHost {
    fn task_started(&self, _task: TaskInfo) {}

    fn task_progress(&self, _key: &str, _label: &str, _current: Option<u64>, _total: Option<u64>) {}

    fn task_finished(&self, _key: &str) {}
}
