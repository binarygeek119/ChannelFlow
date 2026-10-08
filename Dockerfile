# syntax=docker/dockerfile:1

# ── build ChannelFlow ────────────────────────────────────────────────────────
FROM rust:1-bookworm AS rust-build
WORKDIR /src
COPY Cargo.toml Cargo.lock ./
COPY crates ./crates
RUN cargo build --release --locked

# ── ChannelFlow on top of the ErsatzTV next image ────────────────────────────
# next supplies the patched ffmpeg (VAAPI/QSV, libva, Intel iHD) and the
# `ersatztv` binary at /app/ersatztv; ChannelFlow adds the Rust server on top.
# The base is multi-arch (amd64 + arm64), so it is not pinned — the build
# stage above and this stage then always agree on architecture.
# next itself is not started yet; that lands with the playout milestone, when
# ChannelFlow starts writing next's JSON documents.
FROM ersatztv/next:develop

USER root

# Platform layer: packages next does not ship that ChannelFlow needs.
ENV TZ=America/Chicago
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        tzdata \
        python3 \
        ca-certificates \
    && ln -snf "/usr/share/zoneinfo/${TZ}" /etc/localtime \
    && echo "${TZ}" > /etc/timezone \
    && { command -v fc-cache >/dev/null 2>&1 && fc-cache -f || true; } \
    && wget -qO /usr/local/bin/yt-dlp https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    && chmod +x /usr/local/bin/yt-dlp \
    && rm -rf /var/lib/apt/lists/*

COPY --from=rust-build /src/target/release/channelflow /usr/local/bin/channelflow

# The WeatherStar renderers the compositor serves. They ship with the image so
# a fresh install has the graphics without a second download.
COPY vendor/ws4kp /app/channelflow/ws4kp
COPY vendor/ws3kp /app/channelflow/ws3kp

USER ersatztv

ENV CHANNELFLOW_CONFIG=/config \
    CHANNELFLOW_WS4KP=/app/channelflow/ws4kp \
    CHANNELFLOW_WS3KP=/app/channelflow/ws3kp \
    FFMPEG_PATH=/usr/local/bin/ffmpeg \
    PORT=8097 \
    TZ=America/Chicago

EXPOSE 8097

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD wget -qO- http://127.0.0.1:${PORT}/api/health >/dev/null || exit 1

ENTRYPOINT ["/usr/local/bin/channelflow"]
