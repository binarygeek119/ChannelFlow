# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/ChannelFlow.Core/ChannelFlow.Core.csproj

# Build all v3 plugins
RUN for p in plugins/ChannelFlow.Plugin.*/; do \
      echo "Building $p..."; \
      dotnet build "$p" -c Release; \
    done

RUN dotnet publish src/ChannelFlow.Core/ChannelFlow.Core.csproj -c Release -o /app/publish

# Copy plugins into publish dir
RUN mkdir -p /app/publish/plugins
RUN for d in plugins/com.channelflow.*/; do \
      name=$(basename "$d"); \
      mkdir -p "/app/publish/plugins/$name"; \
      find "$d" -name "*.dll" -exec cp {} "/app/publish/plugins/$name/" \; ; \
      find "$d" -name "plugin.json" -exec cp {} "/app/publish/plugins/$name/" \; ; \
    done

# ── Runtime: ErsatzTV next (provides ersatztv binary + patched ffmpeg) ──
FROM ersatztv/next:develop AS runtime

USER root

# Install .NET 10 runtime
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        wget ca-certificates \
    && wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && rm -f /tmp/dotnet-install.sh \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV DATABASE_PROVIDER=sqlite
ENV CONNECTION_STRING="Data Source=/config/channelflow.db"
ENV ERSATZTV_PATH=/app/ersatztv
ENV ERSATZTV_URL=http://127.0.0.1:8409
EXPOSE 8080
VOLUME ["/config", "/config/plugins", "/config/data"]

# ErsatzTV entrypoint compatibility: run as ersatztv user
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

ENTRYPOINT ["docker-entrypoint.sh", "dotnet", "ChannelFlow.Core.dll"]
