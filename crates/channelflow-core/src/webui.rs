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

/// The marker, inside `<config>/webui`, holding the stamp of the tree that was
/// last copied there.
const STAMP_FILE: &str = ".ui-build";

/// A stable fingerprint of a web-UI tree: the relative path and the bytes of
/// every file it holds. The loader compares it with the stamp of what it last
/// copied, so an upgraded image refreshes `<config>/webui` while an unchanged
/// one copies nothing.
pub fn stamp(root: &Path) -> String {
    // FNV-1a: tiny, deterministic, and only ever used to notice a change.
    fn mix(hash: &mut u64, byte: u8) {
        *hash ^= u64::from(byte);
        *hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    fn walk(dir: &Path, root: &Path, hash: &mut u64) {
        let Ok(entries) = std::fs::read_dir(dir) else {
            return;
        };
        let mut paths: Vec<std::path::PathBuf> = entries
            .filter_map(|entry| entry.ok())
            .map(|entry| entry.path())
            .collect();
        paths.sort();
        for path in paths {
            if path.is_dir() {
                walk(&path, root, hash);
            } else if path.is_file() {
                let name = path.strip_prefix(root).unwrap_or(&path);
                for byte in name.to_string_lossy().as_bytes() {
                    mix(hash, *byte);
                }
                if let Ok(bytes) = std::fs::read(&path) {
                    for byte in bytes {
                        mix(hash, byte);
                    }
                }
            }
        }
    }
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    walk(root, root, &mut hash);
    format!("{hash:016x}")
}

/// The stamp of the tree last copied into `<config>/webui`, if one is recorded.
pub fn read_stamp(root: &Path) -> Option<String> {
    std::fs::read_to_string(webui_dir(root).join(STAMP_FILE))
        .ok()
        .map(|text| text.trim().to_string())
        .filter(|text| !text.is_empty())
}

/// Record the stamp of the tree just copied into `<config>/webui`.
pub fn write_stamp(root: &Path, stamp: &str) -> std::io::Result<()> {
    std::fs::write(webui_dir(root).join(STAMP_FILE), format!("{stamp}\n"))
}