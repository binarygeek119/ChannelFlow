//! Build-time facts the About page reports.
//!
//! Cargo re-runs this whenever any file in the package changes (the default
//! when no `rerun-if-changed` line is emitted), so the revision tracks HEAD
//! without forcing a rebuild of the crate when it has not moved.
//!
//! Both values degrade to empty rather than failing: `.git` is in
//! `.dockerignore`, so an image build finds no repository, and the API drops
//! empty rows instead of showing a blank cell.

fn main() {
    println!("cargo:rustc-env=GIT_SHA={}", revision());
    println!("cargo:rustc-env=RUSTC_VERSION={}", rustc_version());
}

fn revision() -> String {
    let git = std::env::var("GIT").unwrap_or_else(|_| "git".to_string());
    captured(&git, &["rev-parse", "--short", "HEAD"]).unwrap_or_default()
}

fn rustc_version() -> String {
    let rustc = std::env::var("RUSTC").unwrap_or_else(|_| "rustc".to_string());
    captured(&rustc, &["--version"]).unwrap_or_default()
}

/// Run a command and return its trimmed stdout, or `None` if it could not be
/// run or exited non-zero.
fn captured(program: &str, args: &[&str]) -> Option<String> {
    let output = std::process::Command::new(program)
        .args(args)
        .output()
        .ok()
        .filter(|output| output.status.success())?;
    let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
    (!stdout.is_empty()).then_some(stdout)
}
