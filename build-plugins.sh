#!/bin/bash
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
ARTIFACTS="$ROOT/artifacts"
rm -rf "$ARTIFACTS"
mkdir -p "$ARTIFACTS"

for p in "$ROOT"/plugins/ChannelFlow.Plugin.*/; do
  name=$(basename "$p" | sed 's/ChannelFlow.Plugin.//')
  echo "Building $name..."
  dotnet build "$p" -c Release
done

for d in "$ROOT"/plugins/com.channelflow.*/; do
  id=$(basename "$d")
  echo "Packaging $id..."
  mkdir -p "$ARTIFACTS/$id"
  cp "$d"/*.dll "$ARTIFACTS/$id/" 2>/dev/null || true
  cp "$d"/plugin.json "$ARTIFACTS/$id/" 2>/dev/null || true
  cd "$ARTIFACTS" && zip -qr "$id.zip" "$id" && cd "$ROOT"
done

echo "Done. Artifacts in $ARTIFACTS/"
ls -la "$ARTIFACTS"/*.zip
