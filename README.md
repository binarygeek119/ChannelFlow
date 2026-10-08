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
  src/main.rs                 CLI, startup, config directory
  src/model.rs                Channel / NewChannel / UpdateChannel
  src/store.rs                file-backed storage, distinct error types
  src/api.rs                  axum routes, HTTP status mapping, embedded UI
  static/                     index.html, app.css, app.js — compiled in
```

Storage is **one JSON file per channel** under `<config>/channels/`, not a database. next is driven entirely by JSON documents, so the on-disk state is already close to the shape you hand to the engine, and nothing has to be installed before the server runs. If a real query layer becomes necessary later, `store.rs` is the only thing that changes.

Storage failures keep their own error type rather than collapsing into `anyhow`, so the API can answer `404` for a missing channel, `409` for a channel number already in use, and `400` for invalid input instead of reporting everything as `500`.

### API

| Method | Path | Result |
|---|---|---|
| `GET` | `/api/health` | status, name, version |
| `GET` | `/api/channels` | all channels, ordered by number |
| `GET` | `/api/channels/{id}` | one channel, `404` if absent |
| `POST` | `/api/channels` | create, `201`; `409` duplicate number, `400` invalid |
| `PUT` | `/api/channels/{id}` | partial update |
| `DELETE` | `/api/channels/{id}` | `204` |

The web UI is served at `/` and compiled into the binary via `include_str!`, so the image needs no asset directory and cannot start with a half-copied web root. A fresh install seeds channel 1 so there is something to look at.

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
