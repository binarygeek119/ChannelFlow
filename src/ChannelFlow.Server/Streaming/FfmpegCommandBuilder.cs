using FinTv.Domain;
using FinTv.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FinTv.Streaming;

public class FfmpegCommandBuilder
{
    private static readonly ConcurrentDictionary<string, CachedVideoCodec> CodecCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> WarnedSoftwareDecodePaths = new(StringComparer.Ordinal);
    private static readonly HashSet<string> NonVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".svg",
        ".mp3", ".flac", ".m4a", ".wav", ".aac", ".ogg", ".opus", ".wma"
    };

    private readonly FfmpegEncodingService _encoding;
    private readonly StreamNormalizationService _normalization;
    private readonly IFfmpegLocator _ffmpeg;
    private readonly ILogger<FfmpegCommandBuilder> _logger;

    public FfmpegCommandBuilder(
        FfmpegEncodingService encoding,
        StreamNormalizationService normalization,
        IFfmpegLocator ffmpeg,
        ILogger<FfmpegCommandBuilder> logger)
    {
        _encoding = encoding;
        _normalization = normalization;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public IReadOnlyList<string> BuildMediaCommand(
        Channel channel,
        string inputPath,
        double startSeconds,
        double durationSeconds,
        string? bugImagePath,
        string? overlayHeadline = null,
        string? alertTickerPath = null,
        string? sourceAspectRatio = null,
        int? sourceWidth = null,
        int? sourceHeight = null,
        string? trueAspectRatio = null,
        bool overlayBug = true,
        bool fadeBugIn = false,
        bool fadeBugOut = false,
        WeatherAlertToneSandwich? alertTones = null,
        string? sourceVideoCodec = null)
    {
        var (width, height) = GetResolution(channel);
        var gpuFilters = CanUseGpuVideoFilters(channel, overlayHeadline, alertTickerPath);
        var context = CreateEncodingContext(width, height, inputPath, sourceVideoCodec, gpuFilters);
        var encodeSeconds = alertTones is { HasTones: true }
            ? alertTones.TotalSeconds
            : durationSeconds;

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-fflags", "+genpts+discardcorrupt"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(context.HardwareDecodeArgs);
        args.AddRange(new[]
        {
            "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-t", encodeSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", inputPath
        });
        AppendMediaVideoGraph(
            args,
            context,
            channel,
            width,
            height,
            bugImagePath,
            overlayHeadline,
            alertTickerPath,
            durationSeconds: encodeSeconds,
            sourceAspectRatio: sourceAspectRatio,
            sourceWidth: sourceWidth,
            sourceHeight: sourceHeight,
            trueAspectRatio: trueAspectRatio,
            overlayBug: overlayBug,
            fadeBugIn: fadeBugIn,
            fadeBugOut: fadeBugOut,
            alertTones: alertTones);
        AppendVideoEncoderArgs(args, context);
        AppendBroadcastAudioFilter(args);
        AppendAacStereo48k(args);
        AppendMpegTsPipe(args);

        return args;
    }

    /// <summary>
    /// Encodes a live RTSP feed into the channel's normalized MPEG-TS for ChannelFlow's
    /// own tuner path (used when ErsatzTV next is off). No seeking or duration limit —
    /// ffmpeg runs until the feed drops or the caller cancels.
    /// </summary>
    public IReadOnlyList<string> BuildRtspCommand(Channel channel, string rtspUri)
    {
        var (width, height) = GetResolution(channel);
        var gpuFilters = CanUseGpuVideoFilters(channel, overlayHeadline: null, alertTickerPath: null);
        var context = CreateEncodingContext(width, height, mediaPath: rtspUri, gpuFilters: gpuFilters);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-rtsp_transport", "tcp",
            "-rtsp_flags", "prefer_tcp",
            "-fflags", "+genpts+discardcorrupt",
            "-probesize", "5000000",
            "-analyzeduration", "10000000",
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_at_eof", "1",
            "-reconnect_delay_max", "5"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(context.HardwareDecodeArgs);
        args.AddRange(new[] { "-i", rtspUri });
        AppendMediaVideoGraph(
            args,
            context,
            channel,
            width,
            height,
            bugImagePath: null,
            overlayHeadline: null,
            alertTickerPath: null,
            overlayBug: false);
        AppendVideoEncoderArgs(args, context);
        AppendBroadcastAudioFilter(args);
        AppendAacStereo48k(args);
        AppendMpegTsPipe(args);

        return args;
    }

    public IReadOnlyList<string> BuildRemoteMediaCommand(
        Channel channel,
        string inputPath,
        double startSeconds,
        double durationSeconds,
        string? bugImagePath,
        IReadOnlyList<SponsorSkipRange>? skipRanges = null,
        string? sourceAspectRatio = null,
        int? sourceWidth = null,
        int? sourceHeight = null,
        string? trueAspectRatio = null,
        bool overlayBug = true,
        string? sourceVideoCodec = null)
    {
        var (width, height) = GetResolution(channel);
        var skipExpr = FfmpegSkipCuts.BuildSelectExpression(skipRanges ?? []);
        var gpuFilters = CanUseGpuVideoFilters(channel, overlayHeadline: null, alertTickerPath: null, skipExpr);
        var context = CreateEncodingContext(width, height, inputPath, sourceVideoCodec, gpuFilters);
        var isRemoteInput = inputPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || inputPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };

        if (isRemoteInput)
        {
            args.AddRange(new[]
            {
                "-reconnect", "1",
                "-reconnect_streamed", "1",
                "-reconnect_delay_max", "5"
            });
        }

        args.AddRange(context.HardwareDeviceArgs);
        var isPipe = string.Equals(inputPath, "pipe:0", StringComparison.Ordinal);
        // YouTube/HLS pipes and googlevideo URLs are not safe for QSV/VAAPI decode (SIGSEGV 139).
        if (!isPipe && !isRemoteInput)
        {
            args.AddRange(context.HardwareDecodeArgs);
        }

        if (isPipe)
        {
            args.AddRange(new[]
            {
                "-fflags", "+genpts+discardcorrupt",
                "-probesize", "5000000",
                "-analyzeduration", "10000000"
            });
        }
        else
        {
            args.AddRange(new[]
            {
                "-fflags", "+genpts+discardcorrupt",
                "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture),
                "-t", durationSeconds.ToString("F3", CultureInfo.InvariantCulture)
            });
        }

        args.AddRange(new[] { "-i", inputPath });
        AppendMediaVideoGraph(
            args,
            context,
            channel,
            width,
            height,
            bugImagePath,
            overlayHeadline: null,
            alertTickerPath: null,
            skipExpr,
            durationSeconds,
            sourceAspectRatio,
            sourceWidth,
            sourceHeight,
            trueAspectRatio,
            overlayBug);
        AppendVideoEncoderArgs(args, context);
        if (!string.IsNullOrEmpty(skipExpr) && !args.Contains("-filter_complex"))
        {
            AppendBroadcastAudioFilter(args, skipExpr);
        }
        else
        {
            AppendBroadcastAudioFilter(args);
        }

        AppendAacStereo48k(args);
        if (isPipe)
        {
            args.AddRange(new[]
            {
                "-t", durationSeconds.ToString("F3", CultureInfo.InvariantCulture)
            });
        }

        AppendMpegTsPipe(args);

        return args;
    }

    public IReadOnlyList<string> BuildMusicCommand(
        Channel channel,
        string audioPath,
        string? albumArtPath,
        string? alertTickerPath = null,
        bool overlayChannelLogo = true,
        double? durationSeconds = null,
        WeatherAlertToneSandwich? alertTones = null)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height, audioPath);
        var logo = overlayChannelLogo && channel.BugPlacement != BugPlacementMode.None
            ? ResolveBugPath(channel)
            : null;
        var filter = _encoding.AdaptFilterComplexForEncoder(
            BuildMusicFilter(width, height, logo, albumArtPath, alertTickerPath),
            context.Encoder);
        var encodeSeconds = alertTones is { HasTones: true }
            ? alertTones.TotalSeconds
            : durationSeconds;

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[] { "-i", audioPath });

        if (!string.IsNullOrWhiteSpace(albumArtPath) && File.Exists(albumArtPath))
        {
            args.AddRange(new[] { "-loop", "1", "-i", albumArtPath });
        }

        if (!string.IsNullOrWhiteSpace(logo) && File.Exists(logo))
        {
            args.AddRange(new[] { "-loop", "1", "-i", logo });
        }

        var audioMap = "0:a";
        if (alertTones is { HasTones: true })
        {
            AppendAlertToneInputs(args, alertTones);
            var firstTone = 1
                + (!string.IsNullOrWhiteSpace(albumArtPath) && File.Exists(albumArtPath) ? 1 : 0)
                + (!string.IsNullOrWhiteSpace(logo) && File.Exists(logo) ? 1 : 0);
            filter += ";" + BuildAlertToneAudioGraph(0, firstTone, alertTones);
            audioMap = "[aout]";
        }

        args.AddRange(new[]
        {
            "-filter_complex", filter,
            "-map", "[vout]",
            "-map", audioMap
        });
        AppendVideoEncoderArgs(args, context, stillImage: true);
        if (encodeSeconds is > 0)
        {
            args.Add("-t");
            args.Add(encodeSeconds.Value.ToString("F3", CultureInfo.InvariantCulture));
        }

        AppendAacStereo48k(args);
        args.AddRange(new[]
        {
            "-shortest",
            "-f", "mpegts",
            "pipe:1"
        });

        return args;
    }

    public IReadOnlyList<string> BuildFullscreenAlertCommand(
        Channel channel,
        string inputPath,
        double startSeconds,
        double durationSeconds,
        string fullscreenPng,
        WeatherAlertToneSandwich? alertTones = null)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height, fullscreenPng, sourceVideoCodec: null, gpuFilters: true);
        var sandwich = alertTones is { HasTones: true } ? alertTones : null;
        var encodeSeconds = sandwich is not null ? sandwich.TotalSeconds : durationSeconds;
        var fps = _normalization.Current.FpsOutput;
        var target = _normalization.Current;
        var aformat = $"aformat=sample_rates={target.AudioSampleRate}:channel_layouts={target.AudioLayout}";
        var duck = WeatherAlertOverlayService.DuckedShowVolume.ToString("F2", CultureInfo.InvariantCulture);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-fflags", "+genpts+discardcorrupt"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[]
        {
            "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-t", encodeSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", inputPath,
            "-loop", "1",
            "-framerate", fps,
            "-t", encodeSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", fullscreenPng
        });
        if (sandwich is not null)
        {
            AppendAlertToneInputs(args, sandwich);
        }

        var video =
            $"[1:v]fps={target.FpsFilter},scale={width}:{height}:force_original_aspect_ratio=increase," +
            $"crop={width}:{height},setsar=1,format=nv12";
        var graph = context.GpuFilters is not null
            ? video + ",hwupload=extra_hw_frames=64" + GpuEncodeMap(context.GpuFilters) + "[vout]"
            : _encoding.AdaptFilterComplexForEncoder(video + "[vout]", context.Encoder);

        graph += $";[0:a]volume={duck},{aformat}[duck]";
        if (sandwich is not null)
        {
            var mixLabels = new List<string> { "[duck]" };
            var extra = 2;
            if (sandwich.HasAttention)
            {
                var att = sandwich.AttentionSeconds.ToString("F3", CultureInfo.InvariantCulture);
                graph += $";[{extra}:a]atrim=0:{att},asetpts=PTS-STARTPTS,{aformat},volume=1.0[att]";
                mixLabels.Add("[att]");
                extra++;
            }

            if (sandwich.HasEnd)
            {
                var end = sandwich.EndSeconds.ToString("F3", CultureInfo.InvariantCulture);
                var delayMs = Math.Max(0, (int)Math.Round((encodeSeconds - sandwich.EndSeconds) * 1000));
                graph += $";[{extra}:a]atrim=0:{end},asetpts=PTS-STARTPTS,{aformat},adelay={delayMs}|{delayMs}[end]";
                mixLabels.Add("[end]");
            }

            graph += $";{string.Concat(mixLabels)}amix=inputs={mixLabels.Count}:duration=first:dropout_transition=0," +
                "aresample=async=1:first_pts=0[aout]";
        }
        else
        {
            graph += ";[duck]aresample=async=1:first_pts=0[aout]";
        }

        args.AddRange(
        [
            "-filter_complex", graph,
            "-map", "[vout]",
            "-map", "[aout]",
            "-t", encodeSeconds.ToString("F3", CultureInfo.InvariantCulture)
        ]);
        AppendVideoEncoderArgs(args, context, stillImage: true);
        AppendAacStereo48k(args);
        AppendMpegTsPipe(args);
        return args;
    }

    public IReadOnlyList<string> BuildEbsCommand(Channel channel, EbsPlaybackPlan plan)
    {
        var (width, height) = GetResolution(channel);
        var duration = Math.Max(30, plan.DurationSeconds).ToString("F0", CultureInfo.InvariantCulture);

        return plan.DisplayMode switch
        {
            EbsDisplayMode.Static => BuildStaticCommand(width, height, duration, plan.AudioMode, plan.MusicPath),
            EbsDisplayMode.ColorBars => BuildColorBarsCommand(width, height, duration, plan.AudioMode, plan.MusicPath),
            EbsDisplayMode.SlateImage => BuildSlateImageCommand(
                width,
                height,
                duration,
                plan.SlateImagePath,
                plan.AudioMode,
                plan.MusicPath),
            _ => BuildColorBarsCommand(width, height, duration, plan.AudioMode, plan.MusicPath)
        };
    }

    public IReadOnlyList<string> BuildEbsCommand(
        Channel channel,
        string slateImagePath,
        string? audioPath,
        double durationSeconds)
    {
        return BuildEbsCommand(channel, new EbsPlaybackPlan
        {
            DisplayMode = EbsDisplayMode.SlateImage,
            AudioMode = string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)
                ? EbsAudioMode.Silence
                : EbsAudioMode.BackgroundMusic,
            SlateImagePath = slateImagePath,
            MusicPath = audioPath,
            DurationSeconds = durationSeconds
        });
    }

    private IReadOnlyList<string> BuildSlateImageCommand(
        int width,
        int height,
        string duration,
        string? slateImagePath,
        EbsAudioMode audioMode,
        string? musicPath)
    {
        if (string.IsNullOrWhiteSpace(slateImagePath) || !File.Exists(slateImagePath))
        {
            return BuildColorBarsCommand(width, height, duration, audioMode, musicPath);
        }

        var context = CreateEncodingContext(width, height, slateImagePath);
        var filter = _encoding.AdaptFilterComplexForEncoder(
            $"[1:v]scale={width}:{height}:force_original_aspect_ratio=decrease," +
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=yuv420p[vout]",
            context.Encoder);

        if (audioMode == EbsAudioMode.BackgroundMusic
            && !string.IsNullOrWhiteSpace(musicPath)
            && File.Exists(musicPath))
        {
            var args = new List<string>
            {
                "-hide_banner",
                "-loglevel", "warning"
            };
            args.AddRange(context.HardwareDeviceArgs);
            args.AddRange(new[]
            {
                "-stream_loop", "-1",
                "-i", musicPath,
                "-loop", "1",
                "-i", slateImagePath,
                "-filter_complex", filter,
                "-map", "[vout]",
                "-map", "0:a"
            });
            AppendVideoEncoderArgs(args, context, stillImage: true);
            args.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", "192k",
                "-t", duration,
                "-shortest",
                "-f", "mpegts",
                "pipe:1"
            });
            return args;
        }

        var silentArgs = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        silentArgs.AddRange(context.HardwareDeviceArgs);
        silentArgs.AddRange(BuildLavfiAudioInput(audioMode));
        silentArgs.AddRange(new[]
        {
            "-loop", "1",
            "-i", slateImagePath,
            "-filter_complex", filter,
            "-map", "[vout]",
            "-map", "0:a"
        });
        AppendVideoEncoderArgs(silentArgs, context, stillImage: true);
        silentArgs.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-t", duration,
            "-shortest",
            "-f", "mpegts",
            "pipe:1"
        });
        return silentArgs;
    }

    private IReadOnlyList<string> BuildColorBarsCommand(
        int width,
        int height,
        string duration,
        EbsAudioMode audioMode,
        string? musicPath)
    {
        var context = CreateEncodingContext(width, height);

        if (audioMode == EbsAudioMode.BackgroundMusic
            && !string.IsNullOrWhiteSpace(musicPath)
            && File.Exists(musicPath))
        {
            var args = new List<string>
            {
                "-hide_banner",
                "-loglevel", "warning"
            };
            args.AddRange(context.HardwareDeviceArgs);
            args.AddRange(new[]
            {
                "-stream_loop", "-1",
                "-i", musicPath,
                "-f", "lavfi",
                "-i", $"smptebars=size={width}x{height}:rate=30",
                "-map", "1:v",
                "-map", "0:a"
            });
            AppendVideoEncoderArgs(args, context, stillImage: true);
            args.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", "192k",
                "-t", duration,
                "-shortest",
                "-f", "mpegts",
                "pipe:1"
            });
            return args;
        }

        var silentArgs = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        silentArgs.AddRange(context.HardwareDeviceArgs);
        silentArgs.AddRange(BuildLavfiAudioInput(audioMode));
        silentArgs.AddRange(new[]
        {
            "-f", "lavfi",
            "-i", $"smptebars=size={width}x{height}:rate=30",
            "-map", "1:v",
            "-map", "0:a"
        });
        AppendVideoEncoderArgs(silentArgs, context, stillImage: true);
        silentArgs.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-t", duration,
            "-shortest",
            "-f", "mpegts",
            "pipe:1"
        });
        return silentArgs;
    }

    private IReadOnlyList<string> BuildStaticCommand(
        int width,
        int height,
        string duration,
        EbsAudioMode audioMode,
        string? musicPath)
    {
        var context = CreateEncodingContext(width, height);

        if (audioMode == EbsAudioMode.BackgroundMusic
            && !string.IsNullOrWhiteSpace(musicPath)
            && File.Exists(musicPath))
        {
            var args = new List<string>
            {
                "-hide_banner",
                "-loglevel", "warning"
            };
            args.AddRange(context.HardwareDeviceArgs);
            args.AddRange(new[]
            {
                "-stream_loop", "-1",
                "-i", musicPath,
                "-f", "lavfi",
                "-i", $"color=c=808080:s={width}x{height}:r=30,format=gray,geq=lum='255*random(0)'",
                "-map", "1:v",
                "-map", "0:a"
            });
            AppendVideoEncoderArgs(args, context);
            args.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", "192k",
                "-t", duration,
                "-shortest",
                "-f", "mpegts",
                "pipe:1"
            });
            return args;
        }

        var silentArgs = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        silentArgs.AddRange(context.HardwareDeviceArgs);
        silentArgs.AddRange(BuildLavfiAudioInput(audioMode));
        silentArgs.AddRange(new[]
        {
            "-f", "lavfi",
            "-i", $"color=c=808080:s={width}x{height}:r=30,format=gray,geq=lum='255*random(0)'",
            "-map", "1:v",
            "-map", "0:a"
        });
        AppendVideoEncoderArgs(silentArgs, context);
        silentArgs.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-t", duration,
            "-shortest",
            "-f", "mpegts",
            "pipe:1"
        });
        return silentArgs;
    }

    private IReadOnlyList<string> BuildLavfiAudioInput(EbsAudioMode audioMode)
    {
        var target = _normalization.Current;
        var rate = target.AudioSampleRate.ToString();
        return audioMode switch
        {
            EbsAudioMode.WhiteNoise => new List<string>
            {
                "-f", "lavfi",
                "-i", $"anoisesrc=color=white:amplitude=0.01:sample_rate={rate}"
            },
            EbsAudioMode.BeepTone => new List<string>
            {
                "-f", "lavfi",
                "-i", $"sine=frequency=960:sample_rate={rate},aeval=val(0)*if(lt(mod(t\\,1)\\,0.5)\\,1\\,0)"
            },
            _ => new List<string>
            {
                "-f", "lavfi",
                "-i", $"anullsrc=channel_layout={target.AudioLayout}:sample_rate={rate}"
            }
        };
    }

    public IReadOnlyList<string> BuildWeatherCommand(
        int width,
        int height,
        double captureFps,
        string? audioPath,
        double? durationSeconds = null,
        WeatherAlertToneSandwich? alertTones = null,
        AspectRatioMode aspect = AspectRatioMode.SixteenNine)
    {
        var target = _normalization.Current;
        var (outW, outH) = _normalization.ResolveSize(aspect);
        _ = width;
        _ = height;
        var fps = captureFps.ToString(CultureInfo.InvariantCulture);
        var hasAudio = !string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath);
        var context = CreateEncodingContext(outW, outH);
        var videoChain =
            $"fps={target.FpsFilter},scale={outW}:{outH}:force_original_aspect_ratio=decrease,pad={outW}:{outH}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,format=yuv420p";
        var vf = _encoding.AdaptVideoFilterForEncoder(videoChain, context.Encoder);
        var encodeSeconds = alertTones is { HasTones: true }
            ? alertTones.TotalSeconds
            : durationSeconds;

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-fflags", "+genpts",
            "-thread_queue_size", "512"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[]
        {
            "-f", "image2pipe",
            "-vcodec", "mjpeg",
            "-framerate", fps,
            "-i", "pipe:0"
        });

        if (hasAudio)
        {
            args.AddRange(new[]
            {
                "-thread_queue_size", "1024",
                // The video side is a real-time pipe fed one JPEG at a time by the compositor,
                // but ffmpeg reads a file input at full decode speed. Without -re the muxed
                // audio timeline races far ahead of the video (measured ~20s of extra audio
                // over a 45s run), so players underrun waiting for audio and the music chops.
                "-re",
                "-stream_loop", "-1",
                "-i", audioPath!
            });
        }
        else
        {
            args.AddRange(new[]
            {
                "-f", "lavfi",
                "-i", $"anullsrc=channel_layout={target.AudioLayout}:sample_rate={target.AudioSampleRate}"
            });
        }

        if (alertTones is { HasTones: true })
        {
            AppendAlertToneInputs(args, alertTones);
            var graph = _encoding.AdaptFilterComplexForEncoder(
                $"[0:v]{videoChain}[vout];{BuildAlertToneAudioGraph(1, 2, alertTones)}",
                context.Encoder);
            args.AddRange(new[]
            {
                "-filter_complex", graph,
                "-map", "[vout]",
                "-map", "[aout]"
            });
        }
        else
        {
            args.AddRange(new[]
            {
                "-vf", vf,
                "-map", "0:v:0",
                "-map", "1:a:0?",
                "-af", BuildBroadcastAudioFilter()
            });
        }

        AppendVideoEncoderArgs(args, context, stillImage: true);
        AppendAacStereo48k(args);
        if (encodeSeconds is > 0)
        {
            args.Add("-t");
            args.Add(encodeSeconds.Value.ToString("F3", CultureInfo.InvariantCulture));
        }

        args.AddRange(new[]
        {
            "-f", "mpegts",
            "-mpegts_flags", "+resend_headers+initial_discontinuity",
            "-flush_packets", "1",
            "pipe:1"
        });

        return args;
    }

    public IReadOnlyList<string> BuildOfflineSlateCommand(Channel channel)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height);
        var args = new List<string>
        {
            "-hide_banner"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[]
        {
            "-f", "lavfi",
            "-i", $"color=c=black:s={width}x{height}:r=30",
            "-vf", $"drawtext=text='{EscapeDrawText(channel.Name)} - Off Air':fontcolor=white:fontsize=36:x=(w-text_w)/2:y=(h-text_h)/2"
        });
        AppendVideoEncoderArgs(args, context);
        args.AddRange(new[]
        {
            "-t", "30",
            "-f", "mpegts",
            "pipe:1"
        });
        return args;
    }

    public IReadOnlyList<string> BuildBlackdetectCommand(string inputPath, string? sourceVideoCodec = null)
    {
        var context = CreateEncodingContext(0, 0, inputPath, sourceVideoCodec);
        var args = new List<string>
        {
            "-hide_banner"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(context.HardwareDecodeArgs);
        args.AddRange(new[]
        {
            "-i", inputPath,
            "-vf", "blackdetect=d=0.5:pix_th=0.10",
            "-an",
            "-f", "null",
            "-"
        });
        return args;
    }

    public StreamPipelineInfo DescribePipeline()
    {
        var target = _normalization.Current;
        var status = _encoding.Describe();
        var encoder = _encoding.ResolveVideoEncoder(target.IsMpeg2);
        return new StreamPipelineInfo(
            $"{target.Summary} via {encoder}",
            encoder,
            status.HardwareAcceleration,
            status.HardwareAcceleration == "none" ? "software" : status.HardwareAcceleration,
            status.VaapiDevice,
            encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase),
            encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase),
            _normalization.Describe());
    }

    public IReadOnlyList<string> BuildTestEncodeCommand()
    {
        var target = _normalization.Current;
        var (width, height) = target.ResolveSize(AspectRatioMode.SixteenNine);
        var context = CreateEncodingContext(width, height);
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(
        [
            "-f", "lavfi",
            "-i", $"color=c=black:s={width}x{height}:r={target.FpsOutput}:d=1",
            "-f", "lavfi",
            "-i", $"anullsrc=channel_layout={target.AudioLayout}:sample_rate={target.AudioSampleRate}:d=1",
            "-vf", _encoding.AdaptVideoFilterForEncoder(
                $"fps={target.FpsFilter},scale={width}:{height}:flags=bicubic,setsar=1,format=yuv420p",
                context.Encoder),
            "-map", "0:v:0",
            "-map", "1:a:0"
        ]);
        AppendVideoEncoderArgs(args, context);
        AppendAacStereo48k(args);
        args.AddRange(["-t", "1", "-f", "null", "-"]);
        return args;
    }

    private readonly record struct EncodingContext(
        string Encoder,
        IReadOnlyList<string> HardwareDeviceArgs,
        IReadOnlyList<string> HardwareDecodeArgs,
        string? GpuFilters);

    private EncodingContext CreateEncodingContext(
        int width,
        int height,
        string? mediaPath = null,
        string? sourceVideoCodec = null,
        bool gpuFilters = false)
    {
        _ = width;
        _ = height;
        var encoder = _encoding.ResolveVideoEncoder(_normalization.Current.IsMpeg2);
        var hardware = encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase)
            || encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase)
            || encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase);
        if (!hardware)
        {
            return new EncodingContext(encoder, [], [], null);
        }

        var filterKind = !gpuFilters
            ? null
            : encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase) ? "qsv"
            : encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase) ? "vaapi"
            : null;
        var codec = ResolveSourceVideoCodec(mediaPath, sourceVideoCodec);
        var decodeArgs = LooksLikeLocalVideo(mediaPath)
            ? _encoding.DecodeArgsForSource(codec, stayOnGpu: filterKind is not null)
            : [];
        if ((_encoding.UseVaapi || _encoding.UseQsv)
            && _encoding.HardwareDecodeArgs.Count > 0
            && decodeArgs.Count == 0
            && WarnedSoftwareDecodePaths.TryAdd($"{mediaPath}|{codec}", 0))
        {
            var reason = _encoding.UseQsv
                ? "no QSV decoder or render node VLD profile"
                : FfmpegEncodingService.IsUnsafeVaapiDecodeCodec(codec)
                    ? "AV1 VAAPI decode is unsafe on Intel iHD"
                    : "render node has no VAAPI VLD profile";
            _logger.LogWarning(
                "Skipping {Accel} decode for {Codec} source {Path} ({Reason}); software-decoding then encoding with {Encoder}",
                _encoding.UseQsv ? "QSV" : "VAAPI",
                FfmpegEncodingService.NormalizeVideoCodec(codec) ?? "unknown",
                mediaPath,
                reason,
                encoder);
        }

        return new EncodingContext(encoder, _encoding.HardwareDeviceArgs, decodeArgs, filterKind);
    }

    private static bool CanUseGpuVideoFilters(
        Channel channel,
        string? overlayHeadline,
        string? alertTickerPath,
        string? skipExpr = null)
    {
        if (!string.IsNullOrEmpty(skipExpr))
        {
            return false;
        }

        if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel))
        {
            return false;
        }

        // Alert ticker is CPU-drawn on a short strip, then overlay_vaapi.
        // Keep GPU scale/pad/encode; do not feed QSV frames into drawtext.
        _ = overlayHeadline;
        _ = alertTickerPath;
        return true;
    }

    private string? ResolveSourceVideoCodec(string? mediaPath, string? hintedCodec)
    {
        if ((!_encoding.UseVaapi && !_encoding.UseQsv) || !LooksLikeLocalVideo(mediaPath))
        {
            return hintedCodec;
        }

        var probed = ProbeVideoCodec(mediaPath!);
        return string.IsNullOrWhiteSpace(probed) ? hintedCodec : probed;
    }

    private static bool LooksLikeLocalVideo(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)
            || mediaPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || mediaPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaPath, "pipe:0", StringComparison.Ordinal)
            || !File.Exists(mediaPath))
        {
            return false;
        }

        var extension = Path.GetExtension(mediaPath);
        return string.IsNullOrEmpty(extension) || !NonVideoExtensions.Contains(extension);
    }

    private string? ProbeVideoCodec(string mediaPath)
    {
        try
        {
            var mtime = File.GetLastWriteTimeUtc(mediaPath);
            if (CodecCache.TryGetValue(mediaPath, out var cached) && cached.WriteTimeUtc == mtime)
            {
                return cached.Codec;
            }

            var probe = ResolveFfprobe();
            var start = new ProcessStartInfo
            {
                FileName = probe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-v");
            start.ArgumentList.Add("error");
            start.ArgumentList.Add("-select_streams");
            start.ArgumentList.Add("v:0");
            start.ArgumentList.Add("-show_entries");
            start.ArgumentList.Add("stream=codec_name");
            start.ArgumentList.Add("-of");
            start.ArgumentList.Add("csv=p=0");
            start.ArgumentList.Add(mediaPath);

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    stdout.AppendLine(e.Data);
                }
            };
            process.BeginOutputReadLine();
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            if (!process.WaitForExit(4000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignored
                }

                return null;
            }

            process.WaitForExit();

            var codec = stdout.ToString()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(codec))
            {
                CodecCache[mediaPath] = new CachedVideoCodec(mtime, codec);
            }

            return codec;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not probe video codec for {Path}", mediaPath);
            return null;
        }
    }

    private string ResolveFfprobe()
    {
        var dir = Path.GetDirectoryName(_ffmpeg.EncoderPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var sibling = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return "ffprobe";
    }

    private readonly record struct CachedVideoCodec(DateTime WriteTimeUtc, string Codec);

    private void AppendVideoEncoderArgs(List<string> args, EncodingContext context, bool stillImage = false)
    {
        var target = _normalization.Current;
        args.Add("-c:v");
        args.Add(context.Encoder);
        if (target.IsMpeg2)
        {
            var bitrate = target.VideoBitrate is "2000k" or "4000k" or "6000k" or "8000k"
                ? target.VideoBitrate
                : "5000k";
            var mpeg2 =
                new List<string>
                {
                    "-b:v", bitrate,
                    "-maxrate", bitrate,
                    "-bufsize", "10000k",
                    "-r", target.FpsOutput,
                    "-g", (stillImage ? Math.Min(12, target.Gop) : target.Gop).ToString(),
                    "-bf", "0"
                };
            if (!context.Encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase)
                && !context.Encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            {
                mpeg2.InsertRange(0, ["-q:v", "4"]);
                mpeg2.Add("-pix_fmt");
                mpeg2.Add("yuv420p");
            }

            args.AddRange(mpeg2);
            return;
        }

        args.AddRange(_encoding.GetVideoEncoderArguments(
            stillImage,
            target.VideoProfile,
            target.Level,
            target.FpsOutput,
            target.Gop,
            target.VideoBitrate));
    }

    private void AppendMediaVideoGraph(
        List<string> args,
        EncodingContext context,
        Channel channel,
        int width,
        int height,
        string? bugImagePath,
        string? overlayHeadline,
        string? alertTickerPath,
        string? skipExpr = null,
        double durationSeconds = 0,
        string? sourceAspectRatio = null,
        int? sourceWidth = null,
        int? sourceHeight = null,
        string? trueAspectRatio = null,
        bool overlayBug = true,
        bool fadeBugIn = false,
        bool fadeBugOut = false,
        WeatherAlertToneSandwich? alertTones = null)
    {
        var bug = overlayBug ? ResolveBugFile(channel, bugImagePath) : null;
        var sandwich = alertTones is { HasTones: true } ? alertTones : null;
        var gpuFilters = context.GpuFilters;
        var tickerPng = IsAlertGraphic(alertTickerPath);
        var gpuTicker = gpuFilters is not null && tickerPng;
        var uploadFirst = gpuFilters is not null && context.HardwareDecodeArgs.Count == 0;
        var linear = gpuFilters == "qsv"
            ? BuildQsvScalePad(width, height, uploadFirst)
            : gpuFilters == "vaapi"
                ? BuildVaapiScalePad(width, height, uploadFirst)
                : BuildLinearVideoFilters(channel, width, height, overlayHeadline, tickerPng ? null : alertTickerPath);
        if (!string.IsNullOrEmpty(skipExpr) && gpuFilters is null)
        {
            linear = $"select='{skipExpr}',setpts=N/FRAME_RATE/TB,{linear}";
        }

        if (string.IsNullOrWhiteSpace(bug) && sandwich is null && !tickerPng)
        {
            args.Add("-vf");
            args.Add(gpuFilters is not null
                ? FinishGpuVideoFilters(linear, gpuFilters)
                : _encoding.AdaptVideoFilterForEncoder(linear, context.Encoder));
            args.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn"]);
            return;
        }

        if (!string.IsNullOrWhiteSpace(bug))
        {
            args.AddRange(
            [
                "-loop", "1",
                "-framerate", _normalization.Current.FpsOutput,
                "-pix_fmt", "rgba",
                "-i", bug
            ]);
        }

        if (sandwich is not null)
        {
            AppendAlertToneInputs(args, sandwich);
        }

        var firstToneIndex = string.IsNullOrWhiteSpace(bug) ? 1 : 2;
        string graph;
        if (gpuFilters is not null)
        {
            var bugWidth = 0;
            var overlayX = "0";
            var overlayY = "0";
            var alpha = string.Empty;
            if (!string.IsNullOrWhiteSpace(bug))
            {
                bugWidth = Math.Clamp(width / 8, 140, 260);
                var position = GetBugOverlay(channel, width, height, sourceAspectRatio, sourceWidth, sourceHeight, trueAspectRatio);
                alpha = ChannelBugLayout.AlphaFilters(fadeBugIn, fadeBugOut, durationSeconds);
                (overlayX, overlayY) = SplitOverlayXy(position);
            }

            graph = BuildGpuVideoGraph(
                linear,
                gpuFilters,
                hasBug: !string.IsNullOrWhiteSpace(bug),
                bugWidth,
                overlayX,
                overlayY,
                alpha,
                gpuTicker,
                width,
                height,
                alertTickerPath);
        }
        else if (string.IsNullOrWhiteSpace(bug))
        {
            graph = $"[0:v]{linear}[vout]";
        }
        else
        {
            var bugWidth = Math.Clamp(width / 8, 140, 260);
            var position = GetBugOverlay(channel, width, height, sourceAspectRatio, sourceWidth, sourceHeight, trueAspectRatio);
            var alpha = ChannelBugLayout.AlphaFilters(fadeBugIn, fadeBugOut, durationSeconds);
            graph =
                $"[0:v]{linear}[base];" +
                $"[1:v]{PrepareTransparentBug(bugWidth, alpha)}[bug];" +
                $"[base][bug]{OverlayTransparentBug(position)}[vout]";
        }

        if (tickerPng && gpuFilters is null)
        {
            graph = graph.Replace("[vout]", "[vpre]", StringComparison.Ordinal)
                + $";{BuildCpuTickerMovie(width, height, alertTickerPath!)}[ticker]"
                + $";[vpre][ticker]overlay=x=0:y=H-h:eof_action=repeat:repeatlast=1[vout]";
        }

        if (!string.IsNullOrEmpty(skipExpr) && sandwich is null)
        {
            graph += $";[0:a]aselect='{skipExpr}',asetpts=N/SR/TB,{BuildBroadcastAudioFilter()}[aout]";
        }
        else if (sandwich is not null)
        {
            graph += ";" + BuildAlertToneAudioGraph(0, firstToneIndex, sandwich);
        }

        var audioMap = sandwich is not null || !string.IsNullOrEmpty(skipExpr)
            ? "[aout]"
            : "0:a:0?";
        args.AddRange(
        [
            "-filter_complex",
            gpuFilters is not null ? graph : _encoding.AdaptFilterComplexForEncoder(graph, context.Encoder),
            "-map", "[vout]",
            "-map", audioMap,
            "-sn",
            "-dn",
            "-shortest"
        ]);
    }

    private static string BuildVaapiScalePad(int width, int height, bool uploadFirst)
    {
        var filters = new List<string>();
        if (uploadFirst)
        {
            filters.Add("format=nv12");
            filters.Add("hwupload=extra_hw_frames=64");
        }

        filters.Add(
            $"scale_vaapi={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2:format=nv12");
        filters.Add($"pad_vaapi={width}:{height}:-1:-1:color=black");
        return string.Join(',', filters);
    }

    private static string BuildQsvScalePad(int width, int height, bool uploadFirst)
    {
        var filters = new List<string>();
        if (uploadFirst)
        {
            filters.Add("format=nv12");
            filters.Add("hwupload=extra_hw_frames=64");
        }
        else
        {
            filters.Add("hwmap=derive_device=vaapi");
        }

        // Stock FFmpeg has no pad_qsv; hwupload/format=qsv ENOSYS on this driver
        // for YouTube pipes. Filter on the parent VAAPI device, map to QSV only
        // for h264_qsv.
        filters.Add(
            $"scale_vaapi={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2:format=nv12");
        filters.Add($"pad_vaapi={width}:{height}:-1:-1:color=black");
        return string.Join(',', filters);
    }

    private static string FinishGpuVideoFilters(string linear, string? gpuFilters)
        => gpuFilters == "qsv" ? linear + ",hwmap=derive_device=qsv" : linear;

    private static string GpuEncodeMap(string? gpuFilters)
        => gpuFilters == "qsv" ? ",hwmap=derive_device=qsv" : string.Empty;

    private string BuildGpuVideoGraph(
        string linear,
        string gpuFilters,
        bool hasBug,
        int bugWidth,
        string overlayX,
        string overlayY,
        string alpha,
        bool gpuTicker,
        int width,
        int height,
        string? alertTickerPath)
    {
        if (!hasBug && !gpuTicker)
        {
            return $"[0:v]{FinishGpuVideoFilters(linear, gpuFilters)}[vout]";
        }

        var graph = $"[0:v]{linear}[base]";
        var current = "base";
        if (hasBug)
        {
            // overlay_vaapi / NV12 drops PNG alpha and fills transparent pixels.
            // Download, composite the RGBA bug in RGB, then upload for encode.
            graph +=
                $";[{current}]hwdownload,format=yuv420p[basecpu]" +
                $";[1:v]{PrepareTransparentBug(bugWidth, alpha)}[bug]" +
                $";[basecpu][bug]{OverlayTransparentBug($"{overlayX}:{overlayY}")}[withbug]";
            current = "withbug";
        }

        if (gpuTicker)
        {
            if (hasBug)
            {
                graph += $";{BuildCpuTickerMovie(width, height, alertTickerPath!)}[ticker]";
                graph += $";[{current}][ticker]overlay=x=0:y=H-h:format=rgb:eof_action=repeat:repeatlast=1[vticked]";
                current = "vticked";
                graph += $";[{current}]format=nv12,hwupload=extra_hw_frames=64{GpuEncodeMap(gpuFilters)}[vout]";
            }
            else
            {
                graph += ";" + BuildGpuTickerSource(width, height, alertTickerPath!);
                graph += $";[{current}][ticker]overlay_vaapi=x=0:y=H-h{GpuEncodeMap(gpuFilters)}[vout]";
            }
        }
        else if (hasBug)
        {
            graph += $";[{current}]format=nv12,hwupload=extra_hw_frames=64{GpuEncodeMap(gpuFilters)}[vout]";
        }

        return graph;
    }

    private string BuildGpuTickerSource(int width, int height, string alertTickerPath)
    {
        var fps = _normalization.Current.FpsOutput;
        var escaped = EscapeFilterPath(alertTickerPath);
        return $"movie='{escaped}':loop=0,{TickerFillScroll(width, height, fps)},format=nv12,hwupload=extra_hw_frames=64[ticker]";
    }

    private string BuildCpuTickerMovie(int width, int height, string alertTickerPath)
    {
        var fps = _normalization.Current.FpsOutput;
        var escaped = EscapeFilterPath(alertTickerPath);
        return $"movie='{escaped}':loop=0,{TickerFillScroll(width, height, fps)}";
    }

    /// <summary>
    /// Fit the seamless ticker PNG to bar height, stretch only if it is narrower than
    /// the frame, duplicate it so the crop can wrap, then slide a full-width window.
    /// </summary>
    private static string TickerFillScroll(int width, int height, string fps)
    {
        var barH = TickerBarHeight(height);
        return
            $"fps={fps}," +
            $"scale=-2:{barH}," +
            $"scale=w='max(iw\\,{width})':h={barH}," +
            "split[ta][tb];[ta][tb]hstack," +
            $"crop={width}:{barH}:'mod(t*90\\,max(iw/2\\,1))':0";
    }

    private static int TickerBarHeight(int height)
    {
        var barH = Math.Max(52, height / 18);
        return barH + (barH & 1);
    }

    private static int TickerFontSize(int height)
        => Math.Max(22, height / 42);

    private static string TickerDrawTextFilter(string escapedPath, int font, string y)
        => $"drawtext=textfile='{escapedPath}':expansion=none:fontcolor=white:fontsize={font}:x=w-mod(t*90\\,w+text_w):y={y}";

    private static (string X, string Y) SplitOverlayXy(string position)
    {
        var colon = position.IndexOf(':');
        if (colon <= 0 || colon >= position.Length - 1)
        {
            return ("W-w-24", "H-h-24");
        }

        return (position[..colon], position[(colon + 1)..]);
    }

    private static string? ResolveBugFile(Channel channel, string? bugImagePath)
    {
        if (channel.BugPlacement == BugPlacementMode.None)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(bugImagePath) && File.Exists(bugImagePath))
        {
            return bugImagePath;
        }

        var resolved = ResolveBugPath(channel);
        return !string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved) ? resolved : null;
    }

    private string BuildLinearVideoFilters(
        Channel channel,
        int width,
        int height,
        string? overlayHeadline,
        string? alertTickerPath)
    {
        var filters = new List<string>
        {
            $"fps={_normalization.Current.FpsFilter}",
            $"scale={width}:{height}:force_original_aspect_ratio=decrease",
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black"
        };

        if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel))
        {
            AppendPastTenseNewsOverlay(filters, width, height, overlayHeadline);
        }

        AppendWeatherAlertTicker(filters, height, alertTickerPath);
        filters.Add("setsar=1");
        filters.Add("format=yuv420p");
        return string.Join(',', filters);
    }

    private static void AppendPastTenseNewsOverlay(List<string> filters, int width, int height, string? headline)
    {
        _ = width;
        var barH = Math.Max(52, height / 18);
        var lowerH = Math.Max(92, height / 11);
        var font = Math.Max(24, height / 38);
        var small = Math.Max(16, height / 50);
        filters.Add($"drawbox=x=0:y=0:w=iw:h={barH}:color=0xe11d48@0.92:t=fill");
        filters.Add($"drawtext=text='BREAKING NEWS':expansion=none:fontcolor=white:fontsize={font}:x=28:y=({barH}-th)/2");
        filters.Add($"drawbox=x=0:y=ih-{lowerH}:w=iw:h={lowerH}:color=0x101010@0.90:t=fill");
        filters.Add($"drawtext=text='PAST TENSE NEWS':expansion=none:fontcolor=0xe11d48:fontsize={small}:x=28:y=h-{lowerH}+10");
        if (string.IsNullOrWhiteSpace(headline))
        {
            return;
        }

        var title = TruncateForDrawText(headline, 72);
        filters.Add($"drawtext=text='{EscapeDrawText(title)}':expansion=none:fontcolor=white:fontsize={font}:x=28:y=h-{lowerH}+{small + 18}");
    }

    private static bool HasAlertTicker(string? alertTickerPath)
        => !string.IsNullOrWhiteSpace(alertTickerPath) && File.Exists(alertTickerPath);

    private static bool IsAlertGraphic(string? path)
    {
        if (!HasAlertTicker(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path!);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendWeatherAlertTicker(List<string> filters, int height, string? alertTickerPath)
    {
        if (!HasAlertTicker(alertTickerPath))
        {
            return;
        }

        var barH = TickerBarHeight(height);
        var font = TickerFontSize(height);
        var escaped = EscapeFilterPath(alertTickerPath!);
        filters.Add($"drawbox=x=0:y=ih-{barH}:w=iw:h={barH}:color=0xc41e3a@0.90:t=fill");
        filters.Add(TickerDrawTextFilter(escaped, font, $"h-{barH}+{(barH - font) / 2}"));
    }

    private static string TruncateForDrawText(string text, int maxChars)
    {
        var trimmed = text.Trim().Replace('\n', ' ').Replace('\r', ' ');
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return trimmed[..Math.Max(1, maxChars - 1)].TrimEnd() + "…";
    }

    private static string BuildMusicFilter(int width, int height, string? logoPath, string? albumArtPath, string? alertTickerPath)
    {
        var baseFilter = $"color=c=0x111111:s={width}x{height}:r=30[base]";
        var current = "[base]";

        if (!string.IsNullOrWhiteSpace(albumArtPath) && File.Exists(albumArtPath))
        {
            baseFilter += $";{current}[1:v]scale={width / 2}:{height / 2}:force_original_aspect_ratio=decrease[art];[base][art]overlay=(W-w)/2:(H-h)/2[tmpv]";
            current = "[tmpv]";
        }

        if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath))
        {
            var logoInput = current == "[tmpv]" ? "2:v" : "1:v";
            baseFilter += $";{current}[{logoInput}]{PrepareTransparentBug(160, $"colorchannelmixer=aa={ChannelBugLayout.Opacity.ToString(CultureInfo.InvariantCulture)}")}[logo];{current}[logo]{OverlayTransparentBug("W-w-40:40")}[vout]";
        }
        else
        {
            baseFilter += $";{current}null[vout]";
        }

        if (HasAlertTicker(alertTickerPath))
        {
            if (IsAlertGraphic(alertTickerPath))
            {
                var escaped = EscapeFilterPath(alertTickerPath!);
                baseFilter = baseFilter.Replace("[vout]", "[vpre]")
                    + $";movie='{escaped}':loop=0,{TickerFillScroll(width, height, "30")}[ticker]"
                    + $";[vpre][ticker]overlay=x=0:y=H-h:eof_action=repeat:repeatlast=1[vout]";
            }
            else
            {
                var barH = TickerBarHeight(height);
                var font = TickerFontSize(height);
                var escaped = EscapeFilterPath(alertTickerPath!);
                baseFilter = baseFilter.Replace("[vout]", "[vpre]")
                    + $";[vpre]drawbox=x=0:y=ih-{barH}:w=iw:h={barH}:color=0xc41e3a@0.90:t=fill[vbar]"
                    + $";[vbar]{TickerDrawTextFilter(escaped, font, $"h-{barH}+{(barH - font) / 2}")}[vout]";
            }
        }

        return baseFilter;
    }

    private static string PrepareTransparentBug(int bugWidth, string alpha)
        => $"format=rgba,scale={bugWidth}:-1:force_original_aspect_ratio=decrease:flags=lanczos,{alpha}";

    /// <summary>
    /// Composite a PNG bug in RGB so per-pixel alpha is kept. yuv420/auto overlay
    /// drops the alpha plane and paints an opaque box.
    /// </summary>
    private static string OverlayTransparentBug(string position)
        => $"overlay={position}:format=rgb:eof_action=repeat:repeatlast=1";

    private static string GetBugOverlay(
        Channel channel,
        int width,
        int height,
        string? sourceAspectRatio,
        int? sourceWidth,
        int? sourceHeight,
        string? trueAspectRatio)
        => ChannelBugLayout.OverlayExpression(
            channel.BugPlacement,
            channel.AspectRatio,
            width,
            height,
            sourceAspectRatio,
            sourceWidth,
            sourceHeight,
            trueAspectRatio);

    private (int Width, int Height) GetResolution(Channel channel)
        => _normalization.ResolveSize(channel.AspectRatio);

    private string BuildBroadcastAudioFilter()
    {
        var target = _normalization.Current;
        return $"aresample=async=1:first_pts=0:ochl={target.AudioLayout},aformat=sample_fmts=fltp:sample_rates={target.AudioSampleRate}:channel_layouts={target.AudioLayout}";
    }

    private void AppendBroadcastAudioFilter(List<string> args, string? skipExpr = null)
    {
        if (args.Contains("-af") || MapsFilterComplexAudio(args))
        {
            return;
        }

        var filter = string.IsNullOrEmpty(skipExpr)
            ? BuildBroadcastAudioFilter()
            : $"aselect='{skipExpr}',asetpts=N/SR/TB,{BuildBroadcastAudioFilter()}";
        args.AddRange(["-af", filter]);
    }

    private static bool MapsFilterComplexAudio(List<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-map" && string.Equals(args[i + 1], "[aout]", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void AppendAacStereo48k(List<string> args)
    {
        var target = _normalization.Current;
        args.AddRange(
        [
            "-c:a", target.EncoderAudioCodec,
            "-b:a", target.AudioBitrate,
            "-ac", target.AudioChannelCount.ToString(),
            "-ar", target.AudioSampleRate.ToString()
        ]);
    }

    private static void AppendMpegTsPipe(List<string> args)
    {
        args.AddRange(
        [
            "-f", "mpegts",
            "-mpegts_flags", "+resend_headers+initial_discontinuity",
            "-muxdelay", "0",
            "-muxpreload", "0",
            "-flush_packets", "1",
            "pipe:1"
        ]);
    }

    private static string? ResolveBugPath(Channel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.ChannelLogoPath) && File.Exists(channel.ChannelLogoPath))
        {
            return channel.ChannelLogoPath;
        }

        if (channel.LogoSetId.HasValue && !string.IsNullOrWhiteSpace(channel.LogoFileName))
        {
            foreach (var logosRoot in FinTvRuntime.Current?.ExistingBundledAssetRoots() ?? [])
            {
                var found = Directory.EnumerateFiles(logosRoot, channel.LogoFileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string EscapeFilterPath(string path)
        => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    private static string EscapeDrawText(string text)
        => text.Replace("\\", "\\\\")
            .Replace("'", "\u2019")
            .Replace(":", "\\:")
            .Replace("%", "\\%");

    private static void AppendAlertToneInputs(List<string> args, WeatherAlertToneSandwich sandwich)
    {
        if (sandwich.HasAttention)
        {
            args.AddRange(["-i", sandwich.AttentionPath!]);
        }

        if (sandwich.HasEnd)
        {
            args.AddRange(["-i", sandwich.EndPath!]);
        }
    }

    private string BuildAlertToneAudioGraph(
        int programAudioInputIndex,
        int firstExtraInputIndex,
        WeatherAlertToneSandwich sandwich)
    {
        var target = _normalization.Current;
        var aformat = $"aformat=sample_rates={target.AudioSampleRate}:channel_layouts={target.AudioLayout}";
        var mid = sandwich.MiddleSeconds.ToString("F3", CultureInfo.InvariantCulture);
        var parts = new List<string>();
        var labels = new List<string>();
        var extra = firstExtraInputIndex;
        if (sandwich.HasAttention)
        {
            var att = sandwich.AttentionSeconds.ToString("F3", CultureInfo.InvariantCulture);
            parts.Add($"[{extra}:a]atrim=0:{att},asetpts=PTS-STARTPTS,{aformat}[att]");
            labels.Add("[att]");
            extra++;
        }

        parts.Add($"[{programAudioInputIndex}:a]atrim=0:{mid},asetpts=PTS-STARTPTS,{aformat}[prog]");
        labels.Add("[prog]");
        if (sandwich.HasEnd)
        {
            var end = sandwich.EndSeconds.ToString("F3", CultureInfo.InvariantCulture);
            parts.Add($"[{extra}:a]atrim=0:{end},asetpts=PTS-STARTPTS,{aformat}[end]");
            labels.Add("[end]");
        }

        parts.Add($"{string.Concat(labels)}concat=n={labels.Count}:v=0:a=1,aresample=async=1:first_pts=0[aout]");
        return string.Join(';', parts);
    }
}

public sealed class WeatherAlertToneSandwich
{
    public string? AttentionPath { get; init; }

    public double AttentionSeconds { get; init; }

    public string? EndPath { get; init; }

    public double EndSeconds { get; init; }

    public double MiddleSeconds { get; init; }

    public bool HasAttention
        => !string.IsNullOrWhiteSpace(AttentionPath) && AttentionSeconds > 0.2;

    public bool HasEnd
        => !string.IsNullOrWhiteSpace(EndPath) && EndSeconds > 0.2;

    public bool HasTones => HasAttention || HasEnd;

    public double TotalSeconds
        => (HasAttention ? AttentionSeconds : 0) + MiddleSeconds + (HasEnd ? EndSeconds : 0);
}

public sealed record StreamPipelineInfo(
    string Summary,
    string Encoder,
    string Acceleration,
    string Hardware,
    string? VaapiDevice,
    bool UseVaapi,
    bool UseQsv,
    object Target);
