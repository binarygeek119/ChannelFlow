#!/usr/bin/env bash
# Place the web UI files into <config>/webui so the loader binary can serve
# them. The canonical copies live in crates/channelflow-core/static/.
#
# Usage: scripts/install-webui.sh [config-dir]   (defaults to ./config)

set -euo pipefail

CONFIG="${1:-config}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$HERE/../crates/channelflow-core/static"

if [ ! -d "$SRC" ]; then
  echo "error: no web UI source at $SRC" >&2
  exit 1
fi

mkdir -p "$CONFIG/webui"
# The UI is a shell (index.html/app.css/app.js) plus per-page folders; copy the
# whole tree so `pages/<name>.{html,css,js}` land beside the shell.
cp -rf "$SRC"/. "$CONFIG/webui/"
# /config/webui serves only the base system: every file a page ships with a
# plugin moves out of here into the plugin's own folder. Remove anything the
# base no longer provides (deleted pages, renamed assets) so stale copies stop
# shadowing the true location.
if [ -d "$CONFIG/webui/pages" ] && [ -d "$SRC/pages" ]; then
  for stale in "$CONFIG"/webui/pages/*; do
    [ -e "$stale" ] || continue
    base="$(basename "$stale")"
    if [ ! -e "$SRC/pages/$base" ]; then
      rm -f "$stale"
    fi
  done
fi
echo "web UI installed into $CONFIG/webui/"
find "$CONFIG/webui" -type f | sed "s#^$CONFIG/webui/##" | sort