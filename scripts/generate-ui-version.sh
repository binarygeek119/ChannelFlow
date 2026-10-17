#!/bin/sh
# Generate .ui-version file from UI_BUILD constant in app.js
# This is used by the Docker entrypoint to detect when the web UI needs updating

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
STATIC_DIR="$SCRIPT_DIR/../crates/channelflow-core/static"
APP_JS="$STATIC_DIR/app.js"
VERSION_FILE="$STATIC_DIR/.ui-version"

if [ ! -f "$APP_JS" ]; then
    echo "Error: app.js not found at $APP_JS" >&2
    exit 1
fi

# Extract UI_BUILD value from app.js (format: const UI_BUILD = "123";)
UI_BUILD=$(grep -oP 'const UI_BUILD = "\K[0-9]+' "$APP_JS")

if [ -z "$UI_BUILD" ]; then
    echo "Error: Could not extract UI_BUILD from $APP_JS" >&2
    exit 1
fi

# Write version to file
echo "$UI_BUILD" > "$VERSION_FILE"
echo "Generated .ui-version: $UI_BUILD"
