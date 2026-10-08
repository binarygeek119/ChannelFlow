<p align="center">
  <img src="logo.png" alt="ChannelFlow-Server" width="320" />
</p>

# ChannelFlow-Server

Simulated live TV for [Jellyfin](https://jellyfin.org). This repository is **ChannelFlow-Server** — a .NET 10 app with a red Jellyfin-style Web UI, PostgreSQL, local library playback, WeatherStar, and news.

Home: [github.com/binarygeek119/ChannelFlow](https://github.com/binarygeek119/ChannelFlow)  
TV client: [github.com/binarygeek119/ChannelFlow-TV-Client](https://github.com/binarygeek119/ChannelFlow-TV-Client)  
Mobile client: [github.com/binarygeek119/ChannelFlow-Mobile-Client](https://github.com/binarygeek119/ChannelFlow-Mobile-Client)  
Commercial detect: [github.com/binarygeek119/ChannelFlow-CommercialDetect](https://github.com/binarygeek119/ChannelFlow-CommercialDetect)  
Commercial Spot Tester: [github.com/binarygeek119/ChannelFlow-CommercialSpotTester](https://github.com/binarygeek119/ChannelFlow-CommercialSpotTester)  
Discord: [discord.gg/w7GK7Zufts](https://discord.gg/w7GK7Zufts)

ChannelFlow talks to media servers itself (Jellyfin now; Emby and Plex connections are placeholders). Add Live TV in Jellyfin with this server's M3U and XMLTV URLs from the **Copy M3U** and **Copy XMLTV** buttons at the top of the web UI.

## What runs where

| ChannelFlow-Server | Jellyfin / other players |
| --- | --- |
| Channels, lineups, playout, FFmpeg MPEG-TS | Add M3U + XMLTV as a Live TV tuner |
| Library connections (Jellyfin, sidecar .nfo, Emby/Plex later) | Same media files ChannelFlow can read |
| Commercials / CommercialBrainz | |
| EBS, logos, AI lineups | |
| WeatherStar 4000/3000 (native compositor, vendored ws4kp/ws3kp) | |
| News RSS + TTS channel | |
| Web UI (username/password) | |

Playback reads **local files**. On **Library → Connections**, set path remaps per server (media-server prefix → ChannelFlow mount), for example `/data/media` → `/media`.

## Requirements

- .NET 10 SDK to build, or a self-contained publish from `scripts/publish-native.sh`
- PostgreSQL (your own instance)
- FFmpeg on PATH (or set `FFMPEG_PATH`). The Docker image is based on [`ersatztv/next`](https://github.com/ErsatzTV/next), which ships [ersatztv-ffmpeg](https://github.com/ErsatzTV/ErsatzTV-ffmpeg) (`/usr/local/bin/ffmpeg`) — the same stack as [ErsatzTV/legacy](https://github.com/ErsatzTV/legacy).
- Jellyfin 10+ (or sidecar folders). Emby and Plex connections can be saved now; catalog sync for those comes later
- The same media paths readable by Jellyfin and ChannelFlow-Server

## Run

Clone with the commercial-detect submodule:

```bash
git clone --recurse-submodules https://github.com/binarygeek119/ChannelFlow.git
# already cloned: git submodule update --init --recursive
```

```bash
cp .env.example .env
# optional: set JELLYFIN_URL. PostgreSQL can be configured in the web UI on first launch.
dotnet publish src/ChannelFlow.Server/ChannelFlow.Server.csproj -c Release
# or: bash scripts/publish-native.sh linux-x64
```

Load the `.env` values into the process environment, then run `ChannelFlow.Server` (from `bin/.../publish` or `artifacts/native/linux-x64`). Listen port is `PORT` (default `8097`). Config, logos, weather, and news files live under `CHANNELFLOW_CONFIG` (default `config` next to the app). Postgres connection details are stored in `database.json` in that folder unless `POSTGRES_HOST` is set (Docker/Unraid). Channel bugs ship in `src/ChannelFlow.Server/wwwroot/images/logos` (copied into the config logos folder on startup). EBS graphics and Off Air slates are in `wwwroot/images/media`, alert/news audio in `wwwroot/audio`, and bundled bumpers in `wwwroot/videos`.

Local from source (Fedora/podman): `bash scripts/dev.sh` starts Postgres on `127.0.0.1:5433` and `dotnet run` on `http://127.0.0.1:8097`.

Docker (ErsatzTV next is bundled — there is no second container to start):

```bash
docker compose up -d
```

That starts PostgreSQL plus one ChannelFlow container running **both** ChannelFlow and the next transcoding engine; only `8097` is published. A native `dotnet run` dev box has no next binary, so it keeps using ChannelFlow's own encoder and the defaults are not forced on.

Then:

1. Open `http://<host>:8097`. On first launch, enter PostgreSQL host/port/database/user/password, then create the admin username and password
2. Open **Library → Connections**. Add a Jellyfin server (URL + API key) or a sidecar folder of local files with `.nfo` metadata. Use **Test server**, refresh libraries, then **Sync catalog**
3. Set path remaps on that server card so ChannelFlow can open the same files
4. Copy the M3U and XMLTV URLs from the top of the web UI. In Jellyfin Live TV, add those as a tuner and guide

Items removed from a media server, or whose remapped local file is gone, are marked missing, then deleted by **Library → Removed items** (or Tasks) after the grace period (default 7 days). **Scan local files** checks each catalog path after remap.

Set `FFMPEG_HWACCEL=vaapi` or `qsv` and pass `/dev/dri` access for Intel VAAPI / Quick Sync. The container ships ersatztv-ffmpeg 8.1.2 (VAAPI, QSV, NVENC, libva 2.23), and it is the **next** engine that does the viewer-facing encode.

In Jellyfin, add ChannelFlow's M3U and XMLTV URLs from the top of the web UI under Live TV (tuner + guide).

## Reverse proxy

ChannelFlow expects to sit behind Nginx Proxy Manager, SWAG, Caddy, or Traefik on a hostname such as `https://channelflow.example.duckdns.org`. Forward `X-Forwarded-Proto`, `X-Forwarded-Host`, and `X-Forwarded-For`. Leave live MPEG-TS unbuffered (`proxy_buffering off` / Caddy `flush_interval -1`) and raise the read timeout (an hour is enough).

Optional env vars:

- `CHANNELFLOW_PUBLIC_URL` — public origin used in M3U/XMLTV when Jellyfin fetches them from another host (example: `https://channelflow.example.duckdns.org`)
- `CHANNELFLOW_PATH_BASE` — only if the UI is served under a subpath such as `/channelflow`

Legacy `FINTV_*` names for those same variables still work.

You can also set **Public base URL** on the General tab. Login cookies stay valid across restarts (keys live in the config folder under `dataprotection`).

Nginx:

```nginx
location / {
    proxy_pass http://127.0.0.1:8097;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;
    proxy_buffering off;
    proxy_request_buffering off;
    proxy_read_timeout 3600s;
    proxy_send_timeout 3600s;
}
```

Caddy:

```caddy
channelflow.example.duckdns.org {
    reverse_proxy 127.0.0.1:8097 {
        flush_interval -1
    }
}
```

## ErsatzTV next (bundled transcoding engine)

The Docker image is built **on top of [`ersatztv/next:develop`](https://github.com/ErsatzTV/next)**, the Rust/HLS rewrite of ErsatzTV. One container runs two processes:

- **ChannelFlow** — scheduling, EPG, commercials, weather, news, EBS, and the compositor for everything next cannot render itself. Listens on `8097`.
- **`ersatztv` (next)** — all video transcoding and HLS. Listens on `8409`, which stays **inside the container**: ChannelFlow proxies next's HLS, so you only ever publish `8097`.

`scripts/container-entrypoint.sh` supervises both. If ChannelFlow exits, next is stopped and the container exits so your restart policy brings the pair back together. If next exits, it is restarted with capped backoff.

No configuration is required on a fresh install — **General → ErsatzTV next (transcoding)** arrives pre-filled with `http://127.0.0.1:8409` and `http://127.0.0.1:8097` and enabled, because both processes share this container's loopback. Empty URLs are filled individually, so a half-configured block (enabled but one URL never saved — the state that used to leave next dark with no warning) is repaired instead of skipped; the toggle is only forced on when both were empty, and everything is skipped when no next binary is present (a native `dotnet run` dev box).

How playout flows:

- ChannelFlow writes `lineup.json`, per-channel `channel.json`, and dynamic playout windows into `{config}/next`.
- Each playout window contains a single *dynamic* item: at every item boundary next asks ChannelFlow `GET /iptv/next/resolve/{channel}` and plays whatever comes back.
  - Movies/TV/music/other real media → next transcodes **the file** directly (hardware accel, exact in/out points) with no ChannelFlow ffmpeg involved.
  - WeatherStar/news/off-air → ChannelFlow's compositors run as a live MPEG-TS HTTP source.
    - Give a weather/news channel an **RTSP stream URL** to play a camera feed instead: next pulls the `rtsp://` source and transcodes it directly (no ChannelFlow compositor). With next off, ChannelFlow encodes the RTSP feed itself.
  - Commercials/art slides/bumpers/YouTube music → a ChannelFlow single-item renderer, keyed so content aligns even though next works ~45 s ahead of wall clock.
  - Bundled video and genuine off-air EBS → a short local temp `.ts`, so next never waits on a live HTTP source for static content.
- ChannelFlow proxies next's HLS behind its own endpoints, so the M3U you give Jellyfin never changes: `…/iptv/next/channel/{n}.m3u8` (master) → `…/iptv/next/session/…` (playlists + segments).
- The M3U always points at next's HLS when it is enabled, and `/iptv/stream/{id}` **redirects** to the same playlist rather than standing up a second ChannelFlow encode. Without next (a dev box), that endpoint keeps serving ChannelFlow's own MPEG-TS.
- EPG is unchanged (ChannelFlow's own XMLTV).

next reads lineup/channel configs **once at startup**, so ChannelFlow content-hashes what it writes and bumps `{config}/next/.generation` only when something really changed; the entrypoint restarts next on that bump. Routine 15-minute rewrites do not bounce live sessions — only adding/renumbering channels, changing normalization, and the midnight playout-window roll do.

Requirements/notes:

- Mount the media share at the **same path** ChannelFlow and Jellyfin use so file paths in playouts resolve.
- `TZ` (and **General → schedule time zone**) decide where the playout windows fall; the image defaults to `America/Chicago`.
- Publish only `8097`. To run next as a separate container instead, point **Next server URL** at its `8409` and **ChannelFlow URL (from inside next)** at ChannelFlow's LAN address, then set `ERSATZTV_PATH` to a path that does not exist so the bundled process stays stopped and the loopback defaults are not forced on.
- **There is no automatic fallback.** If streams go dark, look for `[entrypoint]` and `ersatztv` lines in the container log — the entrypoint logs every next start, stop, crash retry, and config-driven restart.

## Weather and news

WeatherStar graphics are vendored from [ws4kp](https://github.com/netbymatt/ws4kp) and [ws3kp](https://github.com/netbymatt/ws3kp) (MIT) and rendered by the native compositor, then encoded to MPEG-TS with optional Jellyfin music as a bed.

News is a 24/7 channel: RSS feeds from the **News** page, optional TTS, FFmpeg overlay, and bed music.

## Releases

To cut a release, run the **Release** workflow (Actions → Release → Run workflow), type the new version, and leave *Attach the packages to a GitHub Release* checked. One run publishes self-contained `linux-x64` and `win-x64` builds, packages them as `channelflow-server-linux-x64-v<version>.tar.gz` and `channelflow-server-win-x64-v<version>.zip`, pushes the Docker image to `ghcr.io/binarygeek119/channelflow` tagged `v<version>`, `<version>`, and `latest`, and attaches the packages to a `v<version>` GitHub release — creating it if it does not exist yet, or replacing its assets if it does.

Nothing builds on push or pull request: the image and packages only change when you bump the version. The `release` job waits for both the packages and the image, so a failed image build never produces a release.

## License

ChannelFlow-Server code follows this repository's license. WeatherStar vendors keep their upstream MIT licenses.
