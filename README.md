# ChannelFlow 2.0.0

A clean start. This branch is built **on top of [`ErsatzTV/next`](https://github.com/ErsatzTV/next)** — its Docker image is the base, and ChannelFlow adds its layers above it.

Nothing has been brought over from `master` yet.

## The base

`ersatztv/next:develop` ships:

- the patched ffmpeg build (VAAPI/QSV, libva, Intel iHD) at `/usr/local/bin/ffmpeg`
- the `ersatztv` binary at `/app/ersatztv`
- the playout, channel and lineup tooling

The `Dockerfile` starts `FROM` that image and currently adds only a platform layer (timezone data, `python3`, `ca-certificates`, `yt-dlp`). It inherits next's own entrypoint, so as-is it runs next alone:

```bash
docker build -t channelflow:2.0.0 .
docker run --rm -p 8409:8409 -v ./config:/config channelflow:2.0.0
```

Then `http://127.0.0.1:8409/channels.m3u` is next's channel list.

Scaffold a lineup first if `/config` is empty:

```bash
docker run --rm -v ./config:/config channelflow:2.0.0 add-lineup /config/lineup.json --channels 1
```

## The vendored copy of next

`vendor/ersatztv-next/` is a plain copy of [`ErsatzTV/next`](https://github.com/ErsatzTV/next) at `main` commit `46796e9` — the source the base image is built from, sitting next to ChannelFlow's own code. It is a copy, not a submodule or a subtree: no history, no linkage back to upstream.

What's in there that matters most:

- `docker/Dockerfile` — upstream's own image build, the reference for how `ersatztv/next:develop` is produced.
- `schema/` — `playout.json`, `channel_config.json` and `lineup_config.json`. These are the public contract; ChannelFlow writes documents that must match them.
- `crates/` — the Rust workspace (ffpipeline, ersatztv-channel, ersatztv, and the `lib*-sys` FFI crates).
- `examples/` — a worked `playout.json`, `channel.json` and `lineup.json`.

To refresh it, replace the directory from a fresh clone of upstream `main` and update the commit noted above.

## Where this goes next

ChannelFlow comes over one piece at a time, each landing as its own layer in the `Dockerfile`:

1. The .NET runtime and the published server, with a supervisor entrypoint that runs both processes.
2. `/config` layout, Postgres wiring, and the health check.
3. The compositor and the resolver that feeds next its sources.
4. Scheduling, library sync, EBS, and the rest of the app.

## Port

next serves on `8409`. ChannelFlow will publish `8097` and keep next internal, as it does on `master`.
