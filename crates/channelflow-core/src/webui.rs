//! The web UI lives as plain files under `<config>/webui`, so an operator can
//! edit or brand it without rebuilding. The defaults ship compiled into the
//! binary and are written out once on first boot; after that they are served
//! from disk.

use std::path::Path;

const EMBEDDED: &[(&str, &[u8])] = &[
    ("index.html", include_bytes!("../static/index.html")),
    ("app.css", include_bytes!("../static/app.css")),
    ("app.js", include_bytes!("../static/app.js")),
    ("logo.png", include_bytes!("../static/logo.png")),
    ("favicon.ico", include_bytes!("../static/favicon.ico")),
    ("favicon-32x32.png", include_bytes!("../static/favicon-32x32.png")),
    ("favicon-16x16.png", include_bytes!("../static/favicon-16x16.png")),
    ("apple-touch-icon.png", include_bytes!("../static/apple-touch-icon.png")),
];

pub fn webui_dir(root: &Path) -> std::path::PathBuf {
    root.join("webui")
}

/// Write any default file that is missing. Existing files are left untouched,
/// so local edits and branding survive restarts and upgrades.
pub fn ensure(root: &Path) -> Result<(), std::io::Error> {
    let dir = webui_dir(root);
    std::fs::create_dir_all(&dir)?;
    for (name, bytes) in EMBEDDED {
        let path = dir.join(name);
        if !path.exists() {
            std::fs::write(&path, *bytes)?;
        }
    }
    Ok(())
}

/// Read a web-ui file from disk, falling back to the compiled-in copy when the
/// file is missing (a fresh config that has not run `ensure` yet, say).
pub fn read(root: &Path, name: &str) -> std::io::Result<Vec<u8>> {
    std::fs::read(webui_dir(root).join(name)).or_else(|_| {
        EMBEDDED
            .iter()
            .find(|(n, _)| *n == name)
            .map(|(_, bytes)| Ok(bytes.to_vec()))
            .unwrap_or_else(|| {
                Err(std::io::Error::new(
                    std::io::ErrorKind::NotFound,
                    format!("webui file {name}"),
                ))
            })
    })
}