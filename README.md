# ChannelFlow 2.0.0

A clean start, built **on top of [`ErsatzTV/next`](https://github.com/ErsatzTV/next)**. The base image is next's; the server is written in Rust, the same toolchain as next; and nothing from the 1.x codebase has been ported — features come over as they are rebuilt.

Current state: **server + web UI shell** — you can create, edit, enable and delete channels, and every channel is a JSON file on disk. Nothing is streaming yet.

## The base

`ersatztv/next:develop` ships the patched ffmpeg build (VAAPI/QSV, libva, Intel iHD) at `/usr/local/bin/ffmpeg`, the `ersatztv` binary at `/app/ersatztv`, and the playout/channel/lineup tooling.

The image is multi-arch (amd64 + arm64) and is not pinned to a platform, so the Rust build stage and the runtime stage always agree on architecture. A platform layer adds what next does not ship: `tzdata`, `python3`, `ca-certificates` and `yt-dlp`.

`ersatztv` is present in the image but **not started yet** — ChannelFlow does not write next's playout documents until the playout milestone lands.

## The server

```
Cargo.toml                    workspace: core and the plugin SDK
crates/channelflow-core/      the base system — channels, storage, plugin manager
  build.rs                    stamps GIT_SHA + RUSTC_VERSION for the About page
  src/main.rs                 CLI, startup, config directory, plugin loading
  src/model.rs                Channel / NewChannel / UpdateChannel
  src/store.rs                file- or Postgres-backed storage behind one Store
  src/api.rs                  axum routes, HTTP status mapping, embedded UI
  src/plugin/                 the plugin manager: lifecycle, catalog, permissions
crates/channelflow-plugin-api/  the SDK: Plugin trait, manifest, storage, core-data, database, UI
  static/                     index.html, app.css, app.js, logo + favicons — compiled in
```

Plugins live in their own repo, [`ChannelFlow-Plugins`](https://github.com/binarygeek119/ChannelFlow-Plugins), fetched by git dependency: `com.channelflow.ai` (the AI Provider Suite) and `com.channelflow.ersatztv` (the ErsatzTV Transcoding Engine). Each implements the `Plugin` trait against the SDK, names itself in a `plugin.json`, and is loaded, enabled, and route-mounted by the core's plugin manager under `/api/plugins/{id}`.

This is the **modular architecture** in its first pass. The base is `channelflow-core` plus the `channelflow-plugin-api` SDK; features live in plugins. Plugins declare their id, version range against the base, requested permissions, and UI contributions, and get namespaced storage, an HTTP client, a logger, a read-only view of the channels (`api:core:read`), and — with `storage:database` — their own tables in the base's Postgres from `PluginApi`. The **AI page** and the **Transcode page** are both plugins now: each declares a `page` UI contribution and the shell renders the tab **only while that plugin is installed** — no AI provider suite plugin, no AI tab. Every call a page makes goes to the plugin's routes, and its settings live in the plugin's own storage. The schema-driven Transcode form is mirrored from next's own `channel_config.json`, vendored beside the plugin so the contract tests still walk it. Plugins are compiled in today; the same trait and lifecycle are what a dynamic loader will call once plugins ship as shared libraries.

**Plugin repositories** give the same install story Jellyfin has: the plugin repo serves a `manifest.json` (one entry per plugin, each with a `versions[]` list), and a ChannelFlow instance registers that URL and installs from it. Each released version's zip is downloaded for the host's `rid`, its sha256 checked against the manifest, and extracted into `<config>/plugins/.installed/{id}` — deliberately separate from `<config>/plugins/{id}`, which stays the plugin's runtime data. Install does not load the library: the manifest's `entrypoint` records which shared library a loaded plugin will reach for once the dynamic loader and the SDK's ABI entrypoint exist. The bundled plugins are registered repositories themselves:

```bash
curl -X POST http://localhost:8097/api/plugins/repositories \
  -H 'content-type: application/json' \
  -d '{"url":"https://raw.githubusercontent.com/binarygeek119/ChannelFlow-Plugins/main/manifest.json"}'
curl http://localhost:8097/api/plugins/catalog
curl -X POST http://localhost:8097/api/plugins/install \
  -H 'content-type: application/json' \
  -d '{"url":"https://raw.githubusercontent.com/binarygeek119/ChannelFlow-Plugins/main/manifest.json","id":"com.channelflow.ai"}'
```

**Media sources** are plugins that implement the SDK's `MediaSource` contract on top of the usual `Plugin` lifecycle. The first is **Jellyfin**: a `jellyfin` connection form, library sync into its own tables (dedup by title:year:type), per-version file selection, posters under `<config>/Images/posters/…`, and `channelflow_plugin_v1` as its ABI entrypoint. The core registers these sources (so `POST /api/connections` only accepts known kinds), stores the connections, and drives sync.

During a sync a source also reports the items it found (movies, series, albums, artists, music videos) through the SDK's `MediaCatalog` handle, and the base stores them in its **own** catalog (a `media_catalog` table on Postgres, `media_catalog.json` on files). The **Media** page reads that catalog — not the live server — so it works the same for every source: **Movies / TV Shows / Music / Music Videos** tabs, each grouped by the source library and laid out as a Jellyfin-style poster grid (portrait posters for movies/shows/music videos, square art for music, with the title and year under each card), at `/webui/media`.

Storage has two backends behind one `Store`, and either holds the same settings: the channels and each plugin's own data.

**Files** — the default, with nothing to install. One JSON document per channel under `<config>/channels/`, and one file per plugin key under `<config>/plugins/{plugin}/{key}.json`. Plugin files are written `0600` because their data can hold keys.

**Postgres** — set `DATABASE_URL` (or pass `--database-url`) and the same settings live in tables created automatically at startup: `channels` and `plugin_kv` (namespaced per plugin). The first open against an empty database imports whatever the config directory already contains, so moving everything over keeps exactly what the files had; after that Postgres is the only source of truth and nothing is written to the directory. `DATABASE_URL` can point at the same server next uses — ChannelFlow's tables are its own.

A plugin that asks for the `storage:database` permission can also create and query **its own** tables in that Postgres. Names are prefixed with the plugin's id (`create_table("audit", …)` becomes `cf_com_channelflow_ersatztv_audit`), so no plugin can collide with the core or another plugin; the ErsatzTV plugin keeps a change history this way. On the file backend there is no database, so the handle says so and the plugin falls back to key/value storage.

When features became plugins, their settings moved into plugin storage with a one-time migration: the AI provider list (from `<config>/ai.json` / the `ai_settings` row) and the transcode defaults plus per-channel overrides (from `<config>/transcode.json` / the `transcode_settings` row, plus each channel's own patch). The legacy copies are removed once moved.

Storage failures keep their own error type rather than collapsing into `anyhow`, so the API can answer `404` for a missing channel, `409` for a channel number already in use, and `400` for invalid input instead of reporting everything as `500`.

### API

| Method | Path | Result |
|---|---|---|
| `GET` | `/api/health` | status, name, version |
| `GET` | `/api/auth/state` | whether setup is done and this request is logged in |
| `POST` | `/api/auth/login` | log in; sets the `channelflow_session` cookie |
| `POST` | `/api/auth/logout` | invalidate the session |
| `POST` | `/api/auth/setup` | create the Web UI account and finish setup |
| `POST` | `/api/auth/forgot` | write a random reset pin to `<config>/reset-<timestamp>.txt`; one per 10 minutes |
| `POST` | `/api/auth/reset` | match the pin from that file and set a new password |
| `POST` | `/api/setup/database` | connect to Postgres, import the config directory, and switch the running store to it |
| `GET` | `/api/about` | version, build, runtime, and the host facts the About page shows |
| `GET` | `/api/settings/general` | the public and local URLs; on first boot the local URL is detected once (from the address the request arrived on, else the host's primary interface) |
| `PUT` | `/api/settings/general` | store the public/local URLs (each empty or `http(s)://…`) |
| `GET` | `/api/channels` | all channels, ordered by number |
| `GET` | `/api/channels/{id}` | one channel, `404` if absent |
| `POST` | `/api/channels` | create, `201`; `409` duplicate number, `400` invalid |
| `PUT` | `/api/channels/{id}` | partial update |
| `DELETE` | `/api/channels/{id}` | `204` |
| `GET` | `/api/plugins/com.channelflow.ersatztv/` | the Transcode page's field list plus the instance defaults |
| `PUT` | `/api/plugins/com.channelflow.ersatztv/` | replace the defaults, `400` if next's schema would reject it |
| `GET` | `/api/plugins/com.channelflow.ersatztv/channels` | every channel and whether it has overrides |
| `GET` | `/api/plugins/com.channelflow.ersatztv/channels/{id}` | overrides, the defaults, and the effective settings |
| `PUT` | `/api/plugins/com.channelflow.ersatztv/channels/{id}` | store that channel's override patch |
| `DELETE` | `/api/plugins/com.channelflow.ersatztv/channels/{id}` | drop every override |
| `GET` | `/api/plugins/com.channelflow.ersatztv/channels/{id}/changes` | change history from the plugin's own Postgres table |
| `GET` | `/api/plugins` | installed plugins (registry + staged) with manifest, permissions, health |
| `PUT` | `/api/plugins/{id}/update` | update to the newest repository version — compiled-in plugins update with ChannelFlow |
| `PUT` | `/api/plugins/{id}/enable` / `disable` | call the plugin's lifecycle hooks |
| `GET` | `/api/plugins/repositories` | the registered plugin-repository URLs |
| `POST` | `/api/plugins/repositories` | register a repository (`201`); the URL is validated by fetching its manifest first |
| `DELETE` | `/api/plugins/repositories/{id}` | forget a registered repository |
| `GET` | `/api/plugins/catalog` | what is installable across the registered repositories (or pass `?url=` to browse one) |
| `POST` | `/api/plugins/install` | download, verify, and stage a plugin version from a repository |
| `GET` | `/api/plugins/installed` | the installs on disk, one record per plugin id |
| `DELETE` | `/api/plugins/installed/{id}` | remove a plugin; `?drop_database=true` also erases its tables and key/value storage |
| `GET` | `/api/mediasources` | the registered media-source plugins (type ids, connection fields, supported media) |
| `GET` | `/api/connections` | the media-source connections |
| `POST` | `/api/connections` | add a connection for a media source; `400` if the kind is not registered |
| `PUT` | `/api/connections/{id}` | update a connection's config |
| `DELETE` | `/api/connections/{id}` | remove a connection; a media source's rows cascade and its orphan posters are swept |
| `POST` | `/api/connections/{id}/test` | have the connection's media source test it (reachable, key accepted, or what failed) |
| `GET` | `/api/media` | the base's own media catalog: per-tab counts and the rows for an optional `?kind=` (movie, series, album, artist, musicvideo) |
| `GET` | `/api/media/image?path=…` | a poster from `<config>/Images`; paths outside the image store are refused |
| `GET` | `/pages/{name}.{ext}` | a page's own HTML/CSS/JS/assets under `<config>/webui/pages` (paths are confined to that folder) |
| `GET` | `/api/plugins/com.channelflow.ai/` | every AI provider, ordered by priority, plus the next free number; keys are never returned |
| `POST` | `/api/plugins/com.channelflow.ai/providers` | add a provider; `201`; `400` on a duplicate name or priority |
| `PUT` | `/api/plugins/com.channelflow.ai/providers/{id}` | partial update; an omitted `api_key` keeps the stored one, an empty one clears it |
| `DELETE` | `/api/plugins/com.channelflow.ai/providers/{id}` | `204`; `404` if the id is unknown |
| `POST` | `/api/plugins/com.channelflow.ai/test` | try an unsaved provider from the "New provider" tab; always `200`, the result carries `ok` |
| `POST` | `/api/plugins/com.channelflow.ai/providers/{id}/test` | try a saved provider with the form's changes laid over it |
| `POST` | `/api/plugins/com.channelflow.ai/test-all` | walk the providers by priority and report the first that answers |
| `GET` | `/live/channels.m3u` | every channel as an M3U playlist — `503` until the playout milestone |
| `GET` | `/live/xmltv.xml` | the guide — `503` until the playout milestone |
| `GET` | `/live/{n}.m3u8` | one channel's HLS stream — `503` until the playout milestone |

The web UI is plain files under `<config>/webui` (the loader binary never embeds them; `scripts/install-webui.sh` copies the canonical set from `crates/channelflow-core/static/`, and the Dockerfile seeds `/usr/share/channelflow/webui` the same way). The shell is small — `index.html`, shared `app.css` and `app.js` — and each nav tab is its own page under `pages/` with its own file set: `pages/<name>.html`, `pages/<name>.css` and `pages/<name>.js`. Every tab has its own URL (`/webui/channels`, `/webui/media`, …); the shell's router injects the page's markup, links its stylesheet and loads its script on first visit, so a page's files are only fetched when that tab is opened. Everything static is served `no-cache`: these bytes change with the binary but carry no ETag or Last-Modified, so without it a browser could keep an old `app.js` beside a new `index.html` after an upgrade. A fresh install seeds channel 1 so there is something to look at.

The shell keeps the 1.0.0 drawer (260px, near-black/rose), the topbar, the login and the first-boot walkthrough, plus the shared runtime every page uses (`$`, `escapeHtml`, `request`, `taskPopup`, `libraryCard`, …). The topbar has **M3U** and **XMLTV** menus that copy the playlist and guide URLs — local or public, from General Settings — to the clipboard. Real pages so far: **Channels** (the CRUD API), **General Settings** (public/local URLs), **About** (reads `/api/about`), **Credits** (static), **Transcode** (the ErsatzTV plugin), **AI** (the AI plugin), **Plugins** (Installed + Store tabs), **Tasks** (the Jellyfin library-scan scheduler), **Live TV**, **Library** (media-source connections and syncs), and **Media** (the base's own catalog). Other menus land on a per-page placeholder until their wiring lands.

### Plugins and the store

The **Plugins** page has two tabs. **Installed** lists what the instance runs: the compiled-in plugins from a small registry (kept in the core's own key/value namespace, so it rides on files or Postgres, each enabled or disabled) plus anything downloaded and staged. Each row can be updated or removed. **Store** reads the registered repositories' `manifest.json` and lists what is not installed, with an **Install** button and each plugin's banner. [`ChannelFlow-Plugins`](https://github.com/binarygeek119/ChannelFlow-Plugins) is registered automatically on first run (`CHANNELFLOW_PLUGIN_STORE` overrides it); more repositories can be added through `/api/plugins/repositories`.

Installing a plugin that is part of this build simply enables it. Installing one that is not downloads the zip for the host's platform, verifies its sha256 against the manifest, extracts it, and stages it under `<config>/plugins/.installed/` — it starts once dynamic loading lands. Either way it leaves the Store and appears under Installed.

**Plugin pages and where they land in the nav.** A plugin's `plugin.json` can contribute a drawer tab with a `page` entry in `ui_contributions` — `{ "type": "page", "id": "ai", "title": "AI", "path": "/ai", "component": "AiPage" }`. Two optional fields place it: `"section": "top"` (or `"main"`) puts the tab in the top navigation group, anything else leaves it just above the Transcode utility page; and `"order"` (a number; lower first, default `100`) sorts multiple plugin tabs within their group. The tab only exists while the plugin is installed, and install/remove rebuilds the nav immediately.

Removing asks first, because it can be destructive: **drop data** erases the plugin's rows and the tables it created (its key/value storage and every `cf_{plugin}_*` table), so any settings or history it kept are permanently gone; **keep data** leaves them in the database for a reinstall to pick up. The plugin's own storage and tables are otherwise untouched.

### AI settings

The AI page is the **AI Provider Suite plugin** — the base shell renders it, and every call it makes goes to the plugin's routes under `/api/plugins/com.channelflow.ai`. Its provider list is persisted in the plugin's own namespaced storage rather than a core file, so the page behaves exactly as before and the plugin is free to move to its own repo. The page holds a **list** of OpenAI-compatible providers shown as tabs. The first tab is always **New provider** and is what the page opens on: give it a **name**, a **priority**, an **API URL**, an **API key**, and the **chat model**, **TTS model** and **voice** to use. Saving adds the provider as its own tab, where the settings can be edited, saved, or deleted. There is no "OpenAI or Venice" switch on purpose — the URL *is* the choice, so OpenAI itself, a compatible provider, and a model on the local network are the same kind of thing configured in different ways.

Providers are tried in **priority order, lowest number first**, and the app moves on to the next when one cannot be reached — the whole reason to keep more than one. The first provider is used, and the rest are only contacted when the ones above them fail. Priority must be unique, since two providers sharing a number would leave the order to chance; so must the name, because it is the tab's title. The **Failover order** list under the form shows the running order, and **Test failover** walks it for real: it tries providers from lowest priority up and stops at the first that answers, reporting any it had to skip. That walk lives in `openai::failover` and is what the playout milestone's AI calls will use to pick an endpoint.

Each provider's key is treated as a secret. The plugin never returns a key — only whether one is saved per provider — because this API has no authentication and the server listens on every interface by default. The plugin's storage is written `0600` (files) or lives in `plugin_kv` (Postgres), and a provider's form starts its key field blank: a save that leaves it blank sends no key and keeps the stored one, while the field's **Remove** button is what clears it — so "leave it alone" and "get rid of it" stay distinguishable without either of them meaning a stray password-manager fill.

**Test AI** answers "does this provider actually work?" without spending a save. It posts whatever is on the form — including a key typed but not yet stored — and makes three real requests, writing nothing. The first lists models, which tells a wrong address or a rejected key apart from a working one; the second asks the chat model for a one-word reply; the third asks the endpoint to speak one short phrase. A bad key and a bad model look identical from a single failed request, so the result reports every probe with the endpoint's own message, and any HTTP status the endpoint returns is a normal `200` result rather than an error the page has to unwrap. The test passes when both the chat and speech probes answer — listing models stays informational, because a compatible server need not implement `/models` at all. This is the one place the server reaches the network, so `reqwest` with rustls — no OpenSSL — is the one dependency the settings pages added.

### Transcode settings

ErsatzTV next reads `ffmpeg` and `normalization` from a per-channel `channel_config.json`. The ErsatzTV plugin's `transcode.rs` mirrors exactly those two keys — the settings that change how a stream is encoded — and deliberately leaves out `playout` and `fallback`, which describe *what* plays. Two tests in that module walk the vendored `schema/channel_config.json` that ships with the plugin, one asserting the Rust types can hold every field it declares and write it back unchanged, the other asserting the Transcode page's field list names exactly the same set. A field added or renamed upstream fails the build instead of going unwritten.

The page offers next's settings as **instance defaults**; each channel stores only its **differences** from them, deep-merged on top when the settings are read back. That is a third state per field, not two: an absent key inherits, while a key present with `null` is a real value — a channel can say "software encode" or "automatic bitrate" even when the default names a hardware encoder or a number. Because the stored patch is sparse, editing a default still reaches every channel that has not overridden that one field, which is what the Transcode page promises. The per-channel dialog diffs its edited values against the defaults to decide what to store, so a field is marked overridden exactly when it differs; there is no separate toggle to keep in sync.

Overrides are plain JSON rather than a second typed struct, precisely because `Option<T>` cannot tell an absent key from a `null` one. A patch is validated by resolving it onto the defaults and deserialising the result into the typed settings, so anything next would reject — an unknown field, the wrong type, a frame rate outside next's pattern — is a `400` before it reaches the channel file.

### Live TV

The Live TV page lists every channel with the stream URL a player will use, plus the M3U and XMLTV playlist URLs, each with a copy button. Only port `8097` is published and the encoder listens inside the container on another port, so every URL is written against ChannelFlow's own origin: `/live/{n}.m3u8`, `/live/channels.m3u` and `/live/xmltv.xml`. Until the playout milestone produces those streams the routes answer `503` with a sentence rather than `404`, so a player pointed at one gets an honest "not yet" instead of "no such thing" — and the playout milestone repoints the same paths at ErsatzTV next without the page changing. There is no embedded player yet, on purpose: it would need a vendored `hls.js` to render an empty state, and it lands with the encoder.

## The vendored copy of next

`vendor/ersatztv-next/` is a plain copy of `ErsatzTV/next` at `main` commit `46796e9` — not a submodule, not a subtree. It is reference material and is excluded from the Docker build context:

- `docker/Dockerfile` — how `ersatztv/next:develop` is produced.
- `schema/` — `playout.json`, `channel_config.json`, `lineup_config.json`. The public contract ChannelFlow's output must match.
- `crates/` — the Rust workspace.
- `examples/` — worked `playout.json`, `channel.json`, `lineup.json`.

### Refreshing next

```bash
scripts/update-next.sh                   # vendor to the lock's ref (main by default)
scripts/update-next.sh --ref v0.2.0      # vendor a release tag instead
scripts/update-next.sh --check           # exit 1 if the copy is behind (CI/cron friendly)

scripts/check-next-release.sh            # plan: is a newer next release out?
scripts/check-next-release.sh --apply    # act on it
```

One command replaces `vendor/ersatztv-next/` with a fresh export of the requested ref, rewrites the commit quoted above, and records provenance in `vendor/ersatztv-next.lock` — commit, date, whether `schema/` moved, and the base image tag it was taken against.

With no `--ref` it follows the ref already recorded in the lock, falling back to `main` only when there is no lock. That matters once a release has been vendored: defaulting back to `main` would make a bare `--check` report a pinned copy stale and a bare update quietly undo the pin.

The part that earns its keep is the **schema diff**. `schema/` is the contract ChannelFlow's playout output has to satisfy, so if `playout.json`, `channel_config.json` or `lineup_config.json` changes upstream the script prints the diff and flags it in the lock instead of letting it land silently. A `schema_changed = "yes"` in the lock means stop and re-check the writer before trusting anything downstream.

`--image-tag <tag>` also rewrites `FROM ersatztv/next:` in the Dockerfile and records it; `--pull` fetches it afterwards. The report lists upstream's published `vX.Y.Z` tags with push times, so a new release is visible the moment you run it. `--dry-run` does everything except write.

### Watching for a new release

`.github/workflows/check-next.yaml` runs `check-next-release.sh --apply` daily and on demand. It is deliberately narrow about what counts as "new":

- Only **stable releases** are considered — drafts, prereleases and non-version tags such as `develop` are dropped.
- The newest of those is then **compared against the vendored commit**. It only opens a PR when GitHub reports it *ahead*. A release cut from an older `main` reports *behind* and is correctly ignored, so a newer tag is never mistaken for an update just for being newer.
- If `status` is `diverged`, the script refuses to act and exits 1 — vendoring it would drop content we have and it does not.

The PR lands on `next-release-<tag>` and carries the vendored source, the matching Docker base bump when upstream has published that image tag, and the schema verdict, all in one place. Any still-open PR for an older release is closed *after* the new one exists, so a failure part way through leaves the older PR covering us rather than leaving nothing.

Open PRs are recognised by their head branch name, so no label or other bookkeeping has to survive between runs.

The file is committed on both `master` and `2.0.0` because GitHub only runs `schedule` from the default branch — on `2.0.0` alone it would never fire. Either way it checks out `2.0.0` explicitly, so the behaviour is the same before and after that branch becomes the default.

## WeatherStar assets

`vendor/ws4kp/` and `vendor/ws3kp/` are the WeatherStar 4000/3000 renderers, carried over in full from 1.x (including `image-templates/`). They ship into the image at `/app/channelflow/ws4kp` and `/app/channelflow/ws3kp`, exported as `CHANNELFLOW_WS4KP` and `CHANNELFLOW_WS3KP` for the compositor to pick up when it lands.

## First run

The first time you open the web UI, ChannelFlow runs a **setup walkthrough** before anything appears. It lives under `/first-time` with each step at its own URL — `/first-time/welcome`, `/first-time/plugins`, `/first-time/database`, `/first-time/transcoding`, `/first-time/media-source`, `/first-time/addresses`, `/first-time/account`, `/first-time/done` — so any step can be visited or shared directly. Until an account exists any other URL (including `/`) redirects to the walkthrough; once setup is done, `/first-time/*` sends you to `/` where the app opens directly. The walkthrough covers: a short introduction, an explanation of the plugin model (and that some plugins are required), a **database** step where you enter your Postgres connection string — once it connects, the schema is created, anything in the config directory is imported, and the running app switches to it without a restart — then an install of the **ErsatzTV Transcoding Engine** and a **Jellyfin media source** (both downloaded, verified, and staged from the plugin store), an **addresses** step (see below), and finally a request for the **admin username and password** for this instance. The API stays open while setup is incomplete so the walkthrough can work, then locks down.

The **addresses** step shows the **local URL** — the address on your local network — which the server detects once, on that first boot, from the address the request arrived on (so a Docker port mapping resolves to the host's LAN address, not the container's), falling back to the host's primary non-loopback interface. Correct it if it is wrong and add an optional **public URL** for an instance exposed beyond the network. Both are stored and can be changed any time on **General Settings → Addresses**; the local URL is never guessed again after that first boot. `CHANNELFLOW_LOCAL_URL` sets the local URL explicitly — the reliable knob for Docker when the browser connected over `localhost` and the host's LAN address cannot be inferred.

The connection string entered there is written to the config directory, so a later restart opens that same database automatically, the same way `--database-url` or `DATABASE_URL` would. A bad connection string is never saved, so it cannot lock a restart out.

After that, every API call except the auth endpoints and `/api/health` requires a session. Once setup is complete the web UI shows a **login screen**: `POST /api/auth/login` with the account created during setup sets a `channelflow_session` cookie (`HttpOnly`), and the session lives in the store (file or Postgres) so a restart keeps you logged in. The password is never stored — only a salted hash.

**Forgot a password?** The login screen's *Forgot password* flow writes a six-pair random pin to `<config>/reset-<timestamp>.txt` (e.g. `reset-10-16-26-15-45-32.txt`) — a file only someone with filesystem access to the config volume can read, never exposed by the web UI. Enter the pin plus a new password and it is changed; the file is deleted, the old session is revoked, and resets are limited to one every ten minutes.

**Sign out** — every page has a **Log out** button in the top right; it revokes the session and returns to the login screen. If the account is ever lost, the walkthrough is re-entered by removing the `config/setup-complete` file and the auth record from the store.

## Running it

```bash
docker build -t channelflow:2.0.0 .
docker run --rm -p 8097:8097 -v "$PWD/config:/config" channelflow:2.0.0
```

Then open `http://127.0.0.1:8097/`. The walkthrough runs once: finishing it writes `setup-complete` into the config directory, and after that the server redirects `/` and every `/first-time` URL to the Guide at `/webui/guide`. The walkthrough is not served again unless you start over. Every tab is its own URL under `/webui` — `/webui/channels`, `/webui/guide`, `/webui/live`, `/webui/plugins`, `/webui/about`, and the rest.

The container runs as **uid 1000 (`ersatztv`)**, inherited from the next base, so the mounted `config` directory must be writable by that user — `chmod 777 config` for a quick test, or `chown 1000:1000 config` for a real setup. The server says exactly this if the directory is not writable.

Without `DATABASE_URL` everything is stored as JSON files under `/config`. To store everything in Postgres instead, pass a connection string — nothing else changes, and on the first run the existing files are imported:

```bash
docker run --rm -p 8097:8097 \
  -e DATABASE_URL=postgres://channelflow:channelflow@db-host:5432/channelflow \
  -v "$PWD/config:/config" channelflow:2.0.0
```

The **web UI is plain files under `<config>/webui`** — `index.html`, `app.css`, `app.js`, the logo, and the favicons. The binary is a loader: it reads and serves those files from disk, so editing or branding the UI goes live on the next request. On a fresh, empty config the loader seeds `<config>/webui` from the shipped copy (the image keeps it at `/usr/share/channelflow/webui`; `CHANNELFLOW_WEBUI` overrides it), or you can run `scripts/install-webui.sh <config>` to copy the canonical files from the build tree.

From a source checkout: `cargo run --release -p channelflow -- --config ./config --port 8097`, or `--database-url "$DATABASE_URL"` for the same Postgres mode. The scratch-database test in `store.rs` runs only when `TEST_DATABASE_URL` is set.

## Where this goes next

1. **Dynamic loading** — load plugins as shared libraries from `/config/plugins/.installed/{id}` via the manifest's `entrypoint`. The install side already exists (repository URL, catalog, checked downloads, extraction and staging); what is left is the ABI entrypoint in the SDK and the loader that calls it.
2. **Playout writer** — turn `Channel` into next's `channel.json` and `playout.json` under `schema/`, and start `ersatztv` alongside with a supervisor entrypoint.
3. **Compositor** — serve ws4kp frames as an HTTP source next pulls, giving one real weather channel.
4. **Library sync, scheduling, EBS/off-air** — the 1.x features, rebuilt.

## Port

ChannelFlow publishes `8097`. next's `8409` stays internal once it runs behind ChannelFlow.
