# ChannelFlow 2.0.0

A clean start, built **on top of [`ErsatzTV/next`](https://github.com/ErsatzTV/next)**. The base image is next's; the server is written in Rust, the same toolchain as next; and nothing from the 1.x codebase has been ported — features come over as they are rebuilt.

Current state: **server + web UI shell** — you can create, edit, enable and delete channels, and every channel is a JSON file on disk. Nothing is streaming yet.

## The base

`ersatztv/next:develop` ships the patched ffmpeg build (VAAPI/QSV, libva, Intel iHD) at `/usr/local/bin/ffmpeg`, the `ersatztv` binary at `/app/ersatztv`, and the playout/channel/lineup tooling.

The image is multi-arch (amd64 + arm64) and is not pinned to a platform, so the Rust build stage and the runtime stage always agree on architecture. A platform layer adds what next does not ship: `tzdata`, `python3`, `ca-certificates` and `yt-dlp`.

`ersatztv` is present in the image but **not started yet** — ChannelFlow does not write next's playout documents until the playout milestone lands.

## The server

```
Cargo.toml                    workspace, excludes vendor/ersatztv-next
crates/channelflow/
  build.rs                    stamps GIT_SHA + RUSTC_VERSION for the About page
  src/main.rs                 CLI, startup, config directory
  src/model.rs                Channel / NewChannel / UpdateChannel
  src/ai.rs                   OpenAI-compatible endpoint + TTS settings
  src/openai.rs               the test call: a model listing, a chat reply, and one spoken phrase
  src/transcode.rs            next's ffmpeg + normalization settings, defaults + merge
  src/store.rs                file-backed storage, distinct error types
  src/api.rs                  axum routes, HTTP status mapping, embedded UI
  static/                     index.html, app.css, app.js, logo + favicons — compiled in
```

Storage is **one JSON file per channel** under `<config>/channels/`, not a database. next is driven entirely by JSON documents, so the on-disk state is already close to the shape you hand to the engine, and nothing has to be installed before the server runs. If a real query layer becomes necessary later, `store.rs` is the only thing that changes.

Instance transcode defaults live beside them in `<config>/transcode.json`, written on first run so it is a real, hand-editable file rather than an implied set of defaults. The AI page's connection settings live in `<config>/ai.json`, written `0600` — that is the one file holding a secret, so it is not left readable by every account on the host.

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
| `GET` | `/api/ai` | the AI page's settings; the saved key is never returned |
| `PUT` | `/api/ai` | partial update; an omitted `api_key` keeps the stored one, an empty one clears it |
| `POST` | `/api/ai/test` | try the endpoint now with the posted values; always `200`, the result carries `ok` |
| `GET` | `/api/channels/{id}/transcode` | overrides, the defaults, and the effective settings |
| `PUT` | `/api/channels/{id}/transcode` | store that channel's override patch |
| `DELETE` | `/api/channels/{id}/transcode` | drop every override |
| `GET` | `/live/channels.m3u` | every channel as an M3U playlist — `503` until the playout milestone |
| `GET` | `/live/xmltv.xml` | the guide — `503` until the playout milestone |
| `GET` | `/live/{n}.m3u8` | one channel's HLS stream — `503` until the playout milestone |

The web UI is served at `/` and compiled into the binary — the markup, CSS and JS via `include_str!`, the logo and favicons via `include_bytes!` — so the image needs no asset directory and cannot start with a half-copied web root. Everything static is served `no-cache`: these bytes change with the binary but carry no ETag or Last-Modified, so without it a browser could keep an old `app.js` beside a new `index.html` after an upgrade. A fresh install seeds channel 1 so there is something to look at.

The shell is carried over from ChannelFlow 1.0.0 unchanged: the 260px left drawer, all 22 menu items with their icons and group gaps, the near-black/rose palette, and the mark. Five menus are real pages. **Channels** is wired to the CRUD API; **About** reads its App and System tables from `/api/about` and reports plainly that the encoder arrives with the playout milestone; **Credits** is static markup; **Transcode** edits the encoder settings below; **AI** edits the OpenAI connection below. The other 17 menus swap the topbar heading and show a placeholder — their hrefs are intercepted rather than served, so clicking one does not 404. Routing them to real pages is part of the wiring pass.

### AI settings

The AI page is one OpenAI-compatible endpoint: an **API URL**, an **API key**, a **chat model**, a **TTS model** and a **voice**. There is no "OpenAI or Venice" switch on purpose — the URL *is* the choice. Point it at `https://api.openai.com/v1`, at a compatible provider, or at a model on the local network, and the same URLs serve both jobs. The API uses different models for different things, so two are named: the **chat model** for text — the lineup and guide copy the playout milestone will ask for — and the **TTS model** plus **voice** for speech.

The API key is treated as a secret rather than an ordinary setting. `GET /api/ai` returns whether a key is saved but never the key itself, because this API has no authentication and the server listens on every interface by default. `ai.json` is written `0600`, and the page starts the field blank: a save that leaves it blank sends no key and keeps the stored one, while the field's **Remove** button is what clears it — so "leave it alone" and "get rid of it" stay distinguishable without either of them meaning a stray password-manager fill.

**Test AI** answers "does this actually work?" without spending a save. It posts whatever is on the page — including a key typed but not yet stored — to `POST /api/ai/test`, which makes three real requests and writes nothing. The first lists models, which tells a wrong address or a rejected key apart from a working one; the second asks the chat model for a one-word reply; the third asks the endpoint to speak one short phrase. A bad key and a bad model look identical from a single failed request, so the result reports every probe with the endpoint's own message, and any HTTP status the endpoint returns is a normal `200` result rather than an error the page has to unwrap. The test passes when both the chat and speech probes answer — listing models stays informational, because a compatible server need not implement `/models` at all. This is the one place the server reaches the network, so `reqwest` with rustls — no OpenSSL — is the one dependency the settings pages added.

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

From a source checkout: `cargo run --release -p channelflow -- --config ./config --port 8097`.

## Where this goes next

1. **Playout writer** — turn `Channel` into next's `channel.json` and `playout.json` under `schema/`, and start `ersatztv` alongside with a supervisor entrypoint.
2. **Compositor** — serve ws4kp frames as an HTTP source next pulls, giving one real weather channel.
3. **Library sync, scheduling, EBS/off-air** — the 1.x features, rebuilt.

## Port

ChannelFlow publishes `8097`. next's `8409` stays internal once it runs behind ChannelFlow.
