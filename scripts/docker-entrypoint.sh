#!/bin/sh
# ChannelFlow container entrypoint.
#
# Applies the configured time zone before the server starts: glibc and chrono
# resolve local time from /etc/localtime (and /etc/timezone), NOT the TZ
# environment variable, so `-e TZ=Europe/London` only takes effect once those
# files point at the right zoneinfo entry. The image is built with tzdata and a
# default zone; this refreshes it from $TZ at every start.
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

APP_USER="${CHANNELFLOW_USER:-ersatztv}"
if [ "$(id -u)" = "0" ] && command -v gosu >/dev/null 2>&1; then
    exec gosu "${APP_USER}" /usr/local/bin/channelflow "$@"
fi

exec /usr/local/bin/channelflow "$@"
