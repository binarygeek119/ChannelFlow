#!/bin/sh
# Generate .ui-version file from UI_BUILD constant in app.js
# This is used by the Docker entrypoint to detect when the web UI needs updating
# Usage: generate-ui-version.sh [webui_dir]

set -e

# If webui_dir is provided, use it; otherwise use the default relative path
if [ -n "$1" ]; then
    WEBUI_DIR="$1"
else
    SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
    WEBUI_DIR="$SCRIPT_DIR/../crates/channelflow-core/static"
fi

APP_JS="$WEBUI_DIR/app.js"
VERSION_FILE="$WEBUI_DIR/.ui-version"

if [ ! -f "$APP_JS" ]; then
    echo "Error: app.js not found at $APP_JS" >&2
    exit 1
fi

# Extract UI_BUILD value from app.js (format: const UI_BUILD = "123";)
UI_BUILD=$(sed -n 's/.*const UI_BUILD = "\([0-9]\+\)".*/\1/p' "$APP_JS")

if [ -z "$UI_BUILD" ]; then
    echo "Error: Could not extract UI_BUILD from $APP_JS" >&2
    exit 1
fi

# Write version to file
echo "$UI_BUILD" > "$VERSION_FILE"
echo "Generated .ui-version: $UI_BUILD"
