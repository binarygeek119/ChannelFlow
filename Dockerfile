FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-amd64 AS build
WORKDIR /src
COPY global.json ./
COPY src/ChannelFlow.CommercialDetect/ src/ChannelFlow.CommercialDetect/
COPY src/ChannelFlow.Server/ src/ChannelFlow.Server/
COPY logo.png logo-plane.png ./
COPY logo.png src/ChannelFlow.Server/wwwroot/logo.png
COPY logo-plane.png src/ChannelFlow.Server/wwwroot/logo-plane.png
COPY vendor/ws4kp/server/fonts vendor/ws4kp/server/fonts
COPY vendor/ws4kp/server/images/backgrounds vendor/ws4kp/server/images/backgrounds
COPY vendor/ws4kp/server/images/icons/current-conditions vendor/ws4kp/server/images/icons/current-conditions
COPY vendor/ws4kp/server/images/maps/radar vendor/ws4kp/server/images/maps/radar
COPY vendor/ws4kp/server/music/default vendor/ws4kp/server/music/default
COPY vendor/ws3kp/server/fonts vendor/ws3kp/server/fonts
COPY vendor/ws3kp/server/images/backgrounds vendor/ws3kp/server/images/backgrounds
ARG CHANNELFLOW_VERSION=1.0.0
ARG CHANNELFLOW_REVISION=dev
RUN dotnet publish src/ChannelFlow.Server/ChannelFlow.Server.csproj -c Release -o /app/publish \
    /p:Version=1.0.0 \
    /p:InformationalVersion=${CHANNELFLOW_VERSION}+${CHANNELFLOW_REVISION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-amd64 AS dotnet-runtime

# ChannelFlow sits on top of the ErsatzTV next image: next's patched ffmpeg
# (VAAPI/QSV, libva, Intel iHD), its `ersatztv` binary and the playout/channel
# tooling all come from that base, and ChannelFlow adds only the .NET runtime on
# top of it. One container runs both processes; scripts/container-entrypoint.sh
# supervises them and restarts next whenever ChannelFlow rewrites its config.
FROM --platform=linux/amd64 ersatztv/next:develop
ARG CHANNELFLOW_VERSION=1.0.0
ARG CHANNELFLOW_REVISION=dev
USER root
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
ENV TZ=America/Chicago \
    FONTCONFIG_PATH=/etc/fonts \
    DOTNET_ROOT=/usr/share/dotnet \
    PATH="/usr/share/dotnet:${PATH}" \
    ERSATZTV_PATH=/app/ersatztv
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

WORKDIR /app/channelflow
COPY --from=build /app/publish .
COPY scripts/container-entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

ENV CHANNELFLOW_CONFIG=/config \
    FINTV_CONFIG=/config \
    FFMPEG_PATH=/usr/local/bin/ffmpeg \
    CHANNELFLOW_YTDLP_PATH=/usr/local/bin/yt-dlp \
    FINTV_YTDLP_PATH=/usr/local/bin/yt-dlp \
    FFMPEG_HWACCEL=vaapi \
    FFMPEG_VAAPI_DEVICE=/dev/dri/renderD128 \
    CHANNELFLOW_VERSION=${CHANNELFLOW_VERSION} \
    CHANNELFLOW_REVISION=${CHANNELFLOW_REVISION} \
    CHANNELFLOW_PACKAGING=docker \
    PORT=8097 \
    TZ=America/Chicago

EXPOSE 8097
# next's own port stays internal: ChannelFlow proxies its HLS on its own routes,
# so publishing 8409 would only expose a second, competing entry point.
VOLUME ["/config"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8097/health >/dev/null || exit 1
ENTRYPOINT ["/app/entrypoint.sh"]
