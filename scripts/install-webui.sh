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
cp -f "$SRC"/* "$CONFIG/webui/"
echo "web UI installed into $CONFIG/webui/"
ls -1 "$CONFIG/webui"