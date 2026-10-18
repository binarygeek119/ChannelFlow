#!/bin/sh
set -e

CONFIG_DIR="${CONFIG_DIR:-/config}"
WEBUI_DIR="$CONFIG_DIR/webui"
VERSION_FILE="$WEBUI_DIR/.ui-version"
APP_WEBUI="/app/wwwroot"

# Sync webui if version changed
if [ -d "$WEBUI_DIR" ] && [ -f "$VERSION_FILE" ]; then
    STORED_VERSION=$(cat "$VERSION_FILE" 2>/dev/null || echo "")
    CURRENT_VERSION=$(cat "$APP_WEBUI/.ui-version" 2>/dev/null || echo "")
    if [ "$STORED_VERSION" != "$CURRENT_VERSION" ]; then
        echo "[entrypoint] UI version changed: $CURRENT_VERSION -> $STORED_VERSION, syncing..."
        cp -r "$WEBUI_DIR"/* "$APP_WEBUI"/ 2>/dev/null || true
        cp "$VERSION_FILE" "$APP_WEBUI/.ui-version" 2>/dev/null || true
    fi
fi

# Ensure data dirs
mkdir -p "$CONFIG_DIR/data" "$CONFIG_DIR/plugins" "$CONFIG_DIR/logs" "$CONFIG_DIR/data/images" "$CONFIG_DIR/data/playout" "$CONFIG_DIR/data/lineups"

# Sync built-in plugins to /config/plugins if not present
if [ -d "/app/plugins" ]; then
    for d in /app/plugins/*/; do
        name=$(basename "$d")
        if [ ! -d "$CONFIG_DIR/plugins/$name" ]; then
            cp -r "$d" "$CONFIG_DIR/plugins/$name"
        fi
    done
fi

exec "$@"
