#!/bin/sh
# ChannelFlow container entrypoint.
#
# Applies the configured time zone before the server starts: glibc and chrono
# resolve local time from /etc/localtime (and /etc/timezone), NOT the TZ
# environment variable, so `-e TZ=Europe/London` only takes effect once those
# files point at the right zoneinfo entry. The image is built with tzdata and a
# default zone; this refreshes it from $TZ at every start.
#
# Syncs the web UI from the image to the config directory when the UI version
# changes. This ensures users get the latest UI when the image is updated
# without losing any customizations they've made to other config files.
#
# Then it drops from root to the unprivileged app user (`ersatztv`, uid 1000,
# inherited from the ErsatzTV next base) and runs the server.
set -eu

if [ -n "${TZ:-}" ] && [ -f "/usr/share/zoneinfo/${TZ}" ]; then
    ln -snf "/usr/share/zoneinfo/${TZ}" /etc/localtime
    printf '%s\n' "${TZ}" > /etc/timezone
elif [ -n "${TZ:-}" ]; then
    printf 'entrypoint: unknown time zone "%s" (see /usr/share/zoneinfo)\n' "${TZ}" >&2
fi

# Sync the web UI from the image to the config directory when the version changes.
# The UI_BUILD version is extracted from app.js and written to a stamp file in the image.
# On startup, we compare the image version to the config version and sync if different.
CONFIG_DIR="${CHANNELFLOW_CONFIG:-/config}"
IMAGE_VERSION_FILE="/usr/share/channelflow/webui/.ui-version"
CONFIG_VERSION_FILE="${CONFIG_DIR}/webui/.ui-version"

if [ -d /usr/share/channelflow/webui ] && [ -f "$IMAGE_VERSION_FILE" ]; then
    mkdir -p "${CONFIG_DIR}/webui"
    
    # Read versions (empty if file doesn't exist)
    IMAGE_VERSION=$(cat "$IMAGE_VERSION_FILE" 2>/dev/null || echo "")
    CONFIG_VERSION=$(cat "$CONFIG_VERSION_FILE" 2>/dev/null || echo "")
    
    # Sync if versions differ or config version is missing
    if [ "$IMAGE_VERSION" != "$CONFIG_VERSION" ]; then
        echo "Web UI update detected (${CONFIG_VERSION:-none} -> ${IMAGE_VERSION}), syncing..."
        cp -r /usr/share/channelflow/webui/* "${CONFIG_DIR}/webui/"
        echo "Web UI updated to version ${IMAGE_VERSION}"
    fi
fi

APP_USER="${CHANNELFLOW_USER:-ersatztv}"
if [ "$(id -u)" = "0" ] && command -v gosu >/dev/null 2>&1; then
    exec gosu "${APP_USER}" /usr/local/bin/channelflow "$@"
fi

exec /usr/local/bin/channelflow "$@"
