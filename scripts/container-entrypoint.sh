#!/bin/sh
# Container entrypoint: runs ErsatzTV next, the transcoding engine, alongside
# ChannelFlow in a single container.
#
#   * ChannelFlow is the scheduler and compositor. It writes next's lineup.json,
#     per-channel channel.json and playout windows into {CHANNELFLOW_CONFIG}/next
#     on startup and then periodically.
#   * next reads that configuration once at startup, so whenever ChannelFlow
#     actually changes it, it bumps .generation and this script restarts next.
#     The bump is content-hashed, so the routine 15-minute rewrite does not
#     bounce live sessions.
#   * next refuses to run an empty lineup, so a fresh install waits for the first
#     channel instead of crash looping.
#   * If ChannelFlow exits, next is stopped and the container exits so Docker's
#     restart policy brings both back together.
#
# ChannelFlow owns playback end to end: it proxies next's HLS on its own port,
# so only ChannelFlow's port needs publishing.
set -u

CONFIG_DIR="${CHANNELFLOW_CONFIG:-/config}"
NEXT_BIN="${ERSATZTV_PATH:-/app/ersatztv}"
NEXT_DIR="$CONFIG_DIR/next"
LINEUP="${NEXT_LINEUP:-$NEXT_DIR/lineup.json}"
MARKER="$NEXT_DIR/.generation"
HLS_DIR="${NEXT_HLS_DIR:-/tmp/next-hls}"

CF_PID=""
NEXT_PID=""
NEXT_GEN=""
FAILURES=0
WAITING_FOR_CHANNELS=0

log() { printf '[entrypoint] %s\n' "$*"; }

# next exits with "no channels have been loaded" on an empty lineup, so gate on
# the lineup actually naming a channel before starting it.
has_channels() {
    [ -f "$LINEUP" ] || return 1
    python3 -c '
import json, sys
try:
    with open(sys.argv[1]) as handle:
        channels = json.load(handle).get("channels") or []
except Exception:
    sys.exit(1)
sys.exit(0 if channels else 1)
' "$LINEUP" 2>/dev/null
}

start_channelflow() {
    cd /app/channelflow || exit 1
    log "starting ChannelFlow"
    /usr/share/dotnet/dotnet ChannelFlow.Server.dll &
    CF_PID=$!
    log "ChannelFlow started (pid $CF_PID)"
}

start_next() {
    mkdir -p "$HLS_DIR"
    NEXT_GEN="$(cat "$MARKER" 2>/dev/null || printf 'unwritten')"
    "$NEXT_BIN" "$LINEUP" &
    NEXT_PID=$!
    WAITING_FOR_CHANNELS=0
    log "ersatztv next started (pid $NEXT_PID, config generation $NEXT_GEN)"
}

stop_next() {
    [ -n "$NEXT_PID" ] || return 0
    kill "$NEXT_PID" 2>/dev/null
    wait "$NEXT_PID" 2>/dev/null
    NEXT_PID=""
    NEXT_GEN=""
    log "ersatztv next stopped"
}

# Start next when there is something for it to serve. Returns 0 when it started,
# 1 when it is still waiting, so the caller can back off.
maybe_start_next() {
    if ! has_channels; then
        if [ -f "$LINEUP" ] && [ "$WAITING_FOR_CHANNELS" -eq 0 ]; then
            WAITING_FOR_CHANNELS=1
            log "waiting for ChannelFlow to define at least one channel before starting ersatztv next"
        fi
        return 1
    fi

    start_next
    FAILURES=0
    return 0
}

shutdown() {
    log "received stop signal"
    stop_next
    if [ -n "$CF_PID" ]; then
        kill "$CF_PID" 2>/dev/null
        wait "$CF_PID" 2>/dev/null
    fi
    exit 0
}

trap 'shutdown' TERM INT

start_channelflow

while :; do
    if ! kill -0 "$CF_PID" 2>/dev/null; then
        log "ChannelFlow exited; stopping the container"
        stop_next
        exit 1
    fi

    if [ -z "$NEXT_PID" ]; then
        maybe_start_next || sleep 2
    elif ! kill -0 "$NEXT_PID" 2>/dev/null; then
        FAILURES=$((FAILURES + 1))
        NEXT_PID=""
        NEXT_GEN=""
        log "ersatztv next exited; restarting (attempt $FAILURES)"
        delay=$FAILURES
        [ "$delay" -gt 8 ] && delay=8
        sleep "$delay"
        if [ "$FAILURES" -gt 20 ]; then
            # Crash loop: stop burning CPU, but keep watching — a corrected
            # configuration arriving later should still bring it back.
            log "ersatztv next is crash looping; waiting 60s before retrying"
            sleep 60
            FAILURES=0
        fi
        maybe_start_next || sleep 2
    elif [ ! -f "$LINEUP" ]; then
        # Config cleared (integration switched off): do not keep serving a lineup
        # that no longer exists on disk.
        log "lineup.json is gone; stopping ersatztv next"
        stop_next
    else
        CURRENT_GEN="$(cat "$MARKER" 2>/dev/null || printf 'unwritten')"
        if [ "$CURRENT_GEN" != "$NEXT_GEN" ]; then
            log "next configuration changed ($NEXT_GEN -> $CURRENT_GEN); restarting ersatztv next"
            stop_next
            maybe_start_next || sleep 2
        fi
    fi

    sleep 1
done
