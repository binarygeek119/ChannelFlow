# syntax=docker/dockerfile:1

# ChannelFlow 2.0.0 starts from a clean copy of the ErsatzTV next image
# (https://github.com/ErsatzTV/next) and builds on top of it. next supplies the
# patched ffmpeg (VAAPI/QSV, libva, Intel iHD), the `ersatztv` binary, and the
# playout/channel tooling. Everything ChannelFlow adds belongs below this line.
FROM --platform=linux/amd64 ersatztv/next:develop

# next's image leaves us as the `ersatztv` user; drop to root for the package
# layer and restore it so the inherited entrypoint behaves as upstream does.
USER root

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

USER ersatztv

# next's own entrypoint is inherited for now. ChannelFlow's supervisor takes
# over when the server lands on this branch.
EXPOSE 8409
