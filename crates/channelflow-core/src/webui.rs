//! The web UI is plain files under `<config>/webui`. The binary is a loader:
//! those files are the app's interface and are read from disk on every
//! request. Publishing the UI means placing the files in `<config>/webui` —
//! the repository's canonical copies live in `crates/channelflow-core/static/`
//! (and `scripts/install-webui.sh` copies them for a fresh config). Nothing is
//! embedded; if the files are absent the app says so instead of serving a
//! half-app.

use std::path::Path;

pub fn webui_dir(root: &Path) -> std::path::PathBuf {
    root.join("webui")
}

/// Read one web-ui file from `<config>/webui`.
pub fn read(root: &Path, name: &str) -> std::io::Result<Vec<u8>> {
    std::fs::read(webui_dir(root).join(name))
}

/// Seed a fresh config by copying every UI file from a shipped directory
/// (the image keeps the canonical set at `/usr/share/channelflow/webui`).
pub fn copy_from(src: &Path, root: &Path) -> std::io::Result<usize> {
    let dir = webui_dir(root);
    std::fs::create_dir_all(&dir)?;
    let mut count = 0;
    for entry in std::fs::read_dir(src)? {
        let entry = entry?;
        let path = entry.path();
        if path.is_file() {
            let name = path.file_name().unwrap_or_default();
            std::fs::copy(&path, dir.join(name))?;
            count += 1;
        }
    }
    Ok(count)
}