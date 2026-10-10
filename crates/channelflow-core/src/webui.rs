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

/// Read a nested web-ui file (for example `pages/channels.js`). The relative
/// path is rebuilt from its normal components only, so `..`, absolute paths
/// and Windows prefixes cannot escape `<config>/webui`.
pub fn read_nested(root: &Path, relative: &str) -> std::io::Result<Vec<u8>> {
    use std::path::Component;
    let mut safe = std::path::PathBuf::new();
    for part in Path::new(relative).components() {
        match part {
            Component::Normal(segment) => safe.push(segment),
            _ => {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::InvalidInput,
                    "path escapes the web UI directory",
                ))
            }
        }
    }
    std::fs::read(webui_dir(root).join(safe))
}

/// Seed a fresh config by copying every UI file from a shipped directory
/// (the image keeps the canonical set at `/usr/share/channelflow/webui`). The
/// tree is copied recursively so per-page folders (`pages/`) come along.
pub fn copy_from(src: &Path, root: &Path) -> std::io::Result<usize> {
    let dir = webui_dir(root);
    std::fs::create_dir_all(&dir)?;
    copy_tree(src, &dir)
}

fn copy_tree(src: &Path, dest: &Path) -> std::io::Result<usize> {
    let mut count = 0;
    for entry in std::fs::read_dir(src)? {
        let entry = entry?;
        let path = entry.path();
        if path.is_dir() {
            let target = dest.join(entry.file_name());
            std::fs::create_dir_all(&target)?;
            count += copy_tree(&path, &target)?;
        } else if path.is_file() {
            std::fs::copy(&path, dest.join(entry.file_name()))?;
            count += 1;
        }
    }
    Ok(count)
}