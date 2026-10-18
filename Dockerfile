# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/ChannelFlow.Core/ChannelFlow.Core.csproj

# Build all v3 plugins
RUN for p in plugins/ChannelFlow.Plugin.*/; do \
      echo "Building $p..."; \
      dotnet build "$p" -c Release || echo "WARN: failed to build $p"; \
    done

RUN dotnet publish src/ChannelFlow.Core/ChannelFlow.Core.csproj -c Release -o /app/publish

# Copy plugins into publish dir (only com.* installable plugin dirs)
RUN mkdir -p /app/publish/plugins
RUN for d in plugins/com.channelflow.*/; do \
      name=$(basename "$d"); \
      mkdir -p "/app/publish/plugins/$name"; \
      find "$d" -name "*.dll" -exec cp {} "/app/publish/plugins/$name/" \; ; \
      find "$d" -name "plugin.json" -exec cp {} "/app/publish/plugins/$name/" \; ; \
    done

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENV DATABASE_PROVIDER=sqlite
ENV CONNECTION_STRING="Data Source=/config/channelflow.db"
EXPOSE 8080
VOLUME ["/config", "/config/plugins", "/config/data"]
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh
ENTRYPOINT ["docker-entrypoint.sh", "dotnet", "ChannelFlow.Core.dll"]
