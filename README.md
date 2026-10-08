# ChannelFlow 2.0.0

A clean start, built **on top of [`ErsatzTV/next`](https://github.com/ErsatzTV/next)**. The base image is next's; the server is written in Rust, the same toolchain as next; and nothing from the 1.x codebase has been ported — features come over as they are rebuilt.

Current state: **server + web UI shell** — you can create, edit, enable and delete channels, and every channel is a JSON file on disk. Nothing is streaming yet.

## The base

`ersatztv/next:develop` ships the patched ffmpeg build (VAAPI/QSV, libva, Intel iHD) at `/usr/local/bin/ffmpeg`, the `ersatztv` binary at `/app/ersatztv`, and the playout/channel/lineup tooling.

The image is multi-arch (amd64 + arm64) and is not pinned to a platform, so the Rust build stage and the runtime stage always agree on architecture. A platform layer adds what next does not ship: `tzdata`, `python3`, `ca-certificates` and `yt-dlp`.

`ersatztv` is present in the image but **not started yet** — ChannelFlow does not write next's playout documents until the playout milestone lands.

## The server

```
Cargo.toml                    workspace: core, the plugin SDK, and bundled plugins
crates/channelflow-core/      the base system — channels, storage, plugin manager
  build.rs                    stamps GIT_SHA + RUSTC_VERSION for the About page
  src/main.rs                 CLI, startup, config directory, plugin loading
  src/model.rs                Channel / NewChannel / UpdateChannel
  src/transcode.rs            next's ffmpeg + normalization settings (core for now)
  src/store.rs                file- or Postgres-backed storage behind one Store
  src/api.rs                  axum routes, HTTP status mapping, embedded UI
  src/plugin/                 the plugin manager: lifecycle, catalog, permissions
crates/channelflow-plugin-api/  the SDK: Plugin trait, manifest, storage, UI contracts
crates/plugins/ai/            the AI Provider Suite plugin (provider list, tests, failover)
  plugin.json                 the plugin's manifest
  src/ai.rs                   provider list: names, priorities, models
  src/openai.rs               provider tests plus the priority-order failover walk
  src/api.rs                  the routes the AI page calls, mounted under /api/plugins/{id}
  static/                     index.html, app.css, app.js, logo + favicons — compiled in
```

This is the **modular architecture** in its first pass. The base is `channelflow-core` plus the `channelflow-plugin-api` SDK; features that used to be core code now live in plugins that implement the `Plugin` trait. Plugins declare who they are in a `plugin.json` manifest — their id, version range against the base, requested permissions, and UI contributions — and get namespaced storage, an HTTP client, and a logger from `PluginApi`. The manager loads, lists, enables and disables them, and mounts each plugin's routes under `/api/plugins/{id}`. The **AI page is the first feature extracted**: the shell still renders it, but every call it makes goes to the AI plugin's routes, and its provider list is persisted in the plugin's own storage. The Transcode page stays core-owned for now. Plugins are compiled in today; the same trait and lifecycle are what a dynamic loader will call once plugins ship as a `ChannelFlow-Plugins` repo.

The plugin SDK and each plugin live in workspace crates ready to lift out into their own repo: a plugin depends on `channelflow-plugin-api` and nothing else in the base, so splitting is a file move plus a dependency change.

Storage has two backends behind one `Store`, and either holds the same settings: the channels, the instance transcode defaults, and each plugin's own data.

**Files** — the default, with nothing to install. One JSON document per channel under `<config>/channels/`, `<config>/transcode.json`, and one file per plugin key under `<config>/plugins/{plugin}/{key}.json`. Plugin files are written `0600` because their data can hold keys. next is driven by JSON documents itself, so file storage keeps the on-disk state the same shape you would hand to the engine.

**Postgres** — set `DATABASE_URL` (or pass `--database-url`) and the same settings live in tables created automatically at startup: `channels`, the one-row `transcode_settings`, and `plugin_kv` (namespaced per plugin). The first open against an empty database imports whatever the config directory already contains, so moving everything over keeps exactly what the files had; after that Postgres is the only source of truth and nothing is written to the directory. `DATABASE_URL` can point at the same server next uses — ChannelFlow's tables are its own.

When the AI feature became a plugin, its old store — `<config>/ai.json` in files mode, the `ai_settings` row in Postgres — was migrated once into the AI plugin's own storage (`plugin_kv`, or `<config>/plugins/com.channelflow.ai/providers.json`) and the legacy copy is removed.

Storage failures keep their own error type rather than collapsing into `anyhow`, so the API can answer `404` for a missing channel, `409` for a channel number already in use, and `400` for invalid input instead of reporting everything as `500`.

### API

| Method | Path | Result |
|---|---|---|
| `GET` | `/api/health` | status, name, version |
| `GET` | `/api/about` | version, build, runtime, and the host facts the About page shows |
| `GET` | `/api/channels` | all channels, ordered by number |
| `GET` | `/api/channels/{id}` | one channel, `404` if absent |
| `POST` | `/api/channels` | create, `201`; `409` duplicate number, `400` invalid |
| `PUT` | `/api/channels/{id}` | partial update |
| `DELETE` | `/api/channels/{id}` | `204` |
| `GET` | `/api/transcode` | the Transcode page's field list plus the instance defaults |
| `PUT` | `/api/transcode` | replace the defaults, `400` if next's schema would reject it |
| `GET` | `/api/plugins` | the plugin catalog: manifests, permissions, UI contributions, health |
| `PUT` | `/api/plugins/{id}/enable` / `disable` | call the plugin's lifecycle hooks |
| `GET` | `/api/plugins/com.channelflow.ai/` | every AI provider, ordered by priority, plus the next free number; keys are never returned |
| `POST` | `/api/plugins/com.channelflow.ai/providers` | add a provider; `201`; `400` on a duplicate name or priority |
| `PUT` | `/api/plugins/com.channelflow.ai/providers/{id}` | partial update; an omitted `api_key` keeps the stored one, an empty one clears it |
| `DELETE` | `/api/plugins/com.channelflow.ai/providers/{id}` | `204`; `404` if the id is unknown |
| `POST` | `/api/plugins/com.channelflow.ai/test` | try an unsaved provider from the "New provider" tab; always `200`, the result carries `ok` |
| `POST` | `/api/plugins/com.channelflow.ai/providers/{id}/test` | try a saved provider with the form's changes laid over it |
| `POST` | `/api/plugins/com.channelflow.ai/test-all` | walk the providers by priority and report the first that answers |
| `GET` | `/api/channels/{id}/transcode` | overrides, the defaults, and the effective settings |
| `PUT` | `/api/channels/{id}/transcode` | store that channel's override patch |
| `DELETE` | `/api/channels/{id}/transcode` | drop every override |
| `GET` | `/live/channels.m3u` | every channel as an M3U playlist — `503` until the playout milestone |
| `GET` | `/live/xmltv.xml` | the guide — `503` until the playout milestone |
| `GET` | `/live/{n}.m3u8` | one channel's HLS stream — `503` until the playout milestone |

The web UI is served at `/` and compiled into the binary — the markup, CSS and JS via `include_str!`, the logo and favicons via `include_bytes!` — so the image needs no asset directory and cannot start with a half-copied web root. Everything static is served `no-cache`: these bytes change with the binary but carry no ETag or Last-Modified, so without it a browser could keep an old `app.js` beside a new `index.html` after an upgrade. A fresh install seeds channel 1 so there is something to look at.

The shell is carried over from ChannelFlow 1.0.0 unchanged: the 260px left drawer (plus one new **Plugins** item), the near-black/rose palette, and the mark. Six menus are real pages. **Channels** is wired to the CRUD API; **About** reads its App and System tables from `/api/about` and reports plainly that the encoder arrives with the playout milestone; **Credits** is static markup; **Transcode** edits the encoder settings below; **AI** edits the provider list served by the AI plugin; **Plugins** lists what is loaded, what each asked permission for, and toggles them. The other 16 menus swap the topbar heading and show a placeholder — their hrefs are intercepted rather than served, so clicking one does not 404. Routing them to real pages is part of the wiring pass.

### AI settings

The AI page is the **AI Provider Suite plugin** — the base shell renders it, and every call it makes goes to the plugin's routes under `/api/plugins/com.channelflow.ai`. Its provider list is persisted in the plugin's own namespaced storage rather than a core file, so the page behaves exactly as before and the plugin is free to move to its own repo. The page holds a **list** of OpenAI-compatible providers shown as tabs. The first tab is always **New provider** and is what the page opens on: give it a **name**, a **priority**, an **API URL**, an **API key**, and the **chat model**, **TTS model** and **voice** to use. Saving adds the provider as its own tab, where the settings can be edited, saved, or deleted. There is no "OpenAI or Venice" switch on purpose — the URL *is* the choice, so OpenAI itself, a compatible provider, and a model on the local network are the same kind of thing configured in different ways.

Providers are tried in **priority order, lowest number first**, and the app moves on to the next when one cannot be reached — the whole reason to keep more than one. The first provider is used, and the rest are only contacted when the ones above them fail. Priority must be unique, since two providers sharing a number would leave the order to chance; so must the name, because it is the tab's title. The **Failover order** list under the form shows the running order, and **Test failover** walks it for real: it tries providers from lowest priority up and stops at the first that answers, reporting any it had to skip. That walk lives in `openai::failover` and is what the playout milestone's AI calls will use to pick an endpoint.

Each provider's key is treated as a secret. The plugin never returns a key — only whether one is saved per provider — because this API has no authentication and the server listens on every interface by default. The plugin's storage is written `0600` (files) or lives in `plugin_kv` (Postgres), and a provider's form starts its key field blank: a save that leaves it blank sends no key and keeps the stored one, while the field's **Remove** button is what clears it — so "leave it alone" and "get rid of it" stay distinguishable without either of them meaning a stray password-manager fill.

**Test AI** answers "does this provider actually work?" without spending a save. It posts whatever is on the form — including a key typed but not yet stored — and makes three real requests, writing nothing. The first lists models, which tells a wrong address or a rejected key apart from a working one; the second asks the chat model for a one-word reply; the third asks the endpoint to speak one short phrase. A bad key and a bad model look identical from a single failed request, so the result reports every probe with the endpoint's own message, and any HTTP status the endpoint returns is a normal `200` result rather than an error the page has to unwrap. The test passes when both the chat and speech probes answer — listing models stays informational, because a compatible server need not implement `/models` at all. This is the one place the server reaches the network, so `reqwest` with rustls — no OpenSSL — is the one dependency the settings pages added.

### Transcode settings

ErsatzTV next reads `ffmpeg` and `normalization` from a per-channel `channel_config.json`. `src/transcode.rs` mirrors exactly those two keys — the settings that change how a stream is encoded — and deliberately leaves out `playout` and `fallback`, which describe *what* plays. Two tests in that module walk `vendor/ersatztv-next/schema/channel_config.json`, one asserting the Rust types can hold every field it declares and write it back unchanged, the other asserting the Transcode page's field list names exactly the same set. A field added or renamed upstream fails the build instead of going unwritten.

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

## Running it

```bash
docker build -t channelflow:2.0.0 .
docker run --rm -p 8097:8097 -v "$PWD/config:/config" channelflow:2.0.0
```

Then open `http://127.0.0.1:8097/`.

The container runs as **uid 1000 (`ersatztv`)**, inherited from the next base, so the mounted `config` directory must be writable by that user — `chmod 777 config` for a quick test, or `chown 1000:1000 config` for a real setup. The server says exactly this if the directory is not writable.

Without `DATABASE_URL` everything is stored as JSON files under `/config`. To store everything in Postgres instead, pass a connection string — nothing else changes, and on the first run the existing files are imported:

```bash
docker run --rm -p 8097:8097 \
  -e DATABASE_URL=postgres://channelflow:channelflow@db-host:5432/channelflow \
  -v "$PWD/config:/config" channelflow:2.0.0
```

From a source checkout: `cargo run --release -p channelflow -- --config ./config --port 8097`, or `--database-url "$DATABASE_URL"` for the same Postgres mode. The scratch-database test in `store.rs` runs only when `TEST_DATABASE_URL` is set.

## Where this goes next

1. **Extract the next feature** — move the transcode settings into a `channelflow-plugin-ersatztv` crate (the AI plugin is the pattern), then lift the plugin crates into the `ChannelFlow-Plugins` repo with the base depending on them by git.
2. **Dynamic loading** — load plugins as shared libraries from `/config/plugins` via the manifest's `entrypoint`, with the install/update/rollback flow and the repo index feeding the catalog.
3. **Playout writer** — turn `Channel` into next's `channel.json` and `playout.json` under `schema/`, and start `ersatztv` alongside with a supervisor entrypoint.
4. **Compositor** — serve ws4kp frames as an HTTP source next pulls, giving one real weather channel.
5. **Library sync, scheduling, EBS/off-air** — the 1.x features, rebuilt.

## Port

ChannelFlow publishes `8097`. next's `8409` stays internal once it runs behind ChannelFlow.
