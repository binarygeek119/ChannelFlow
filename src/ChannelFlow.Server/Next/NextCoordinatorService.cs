using System.Globalization;
using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using FinTv.Services;
using FinTv.Streaming;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Next;

/// <summary>
/// Writes the ErsatzTV next configuration (lineup.json, per-channel channel.json,
/// and per-channel dynamic playout windows) into ChannelFlow's appdata folder so a
/// next container mounted on that folder can serve every enabled channel.
///
/// Playout windows contain a single <see cref="NextDynamicSource"/> placeholder;
/// next resolves it over HTTP on every item boundary, which is how ChannelFlow
/// "pushes things to play" from its own schedule database.
/// </summary>
public sealed class NextCoordinatorService
{
    private readonly ILogger<NextCoordinatorService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FfmpegEncodingService _encoding;

    public NextCoordinatorService(
        ILogger<NextCoordinatorService> logger,
        IServiceScopeFactory scopeFactory,
        FfmpegEncodingService encoding)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _encoding = encoding;
    }

    public static NextTranscodingSettings? Configured
        => FinTvRuntime.Current?.Configuration.NextTranscoding;

    /// <summary>
    /// Whether the next integration is fully configured (enabled + base URLs + token).
    /// </summary>
    public static bool IsEnabled
        => Configured is { Enabled: true } next
            && !string.IsNullOrWhiteSpace(next.BaseUrl)
            && !string.IsNullOrWhiteSpace(next.ResolverBaseUrl)
            && !string.IsNullOrWhiteSpace(next.ResolverToken);

    /// <summary>
    /// Host folder where the next configuration is written
    /// (mount this into the next container at <c>NextConfigFolder</c>).
    /// </summary>
    public static string HostFolder
        => Path.Combine(FinTvRuntime.Current?.DataFolder ?? "data", "next");

    /// <summary>
    /// (Re)generates lineup.json, per-channel channel.json, and the playout windows.
    /// Idempotent; safe to call on startup, on settings save, and periodically.
    /// </summary>
    public async Task WriteConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var next = Configured;
        if (next is null || !next.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(next.BaseUrl)
            || string.IsNullOrWhiteSpace(next.ResolverBaseUrl)
            || string.IsNullOrWhiteSpace(next.ResolverToken))
        {
            _logger.LogWarning(
                "ErsatzTV next is enabled but BaseUrl/ResolverBaseUrl/ResolverToken are missing; skipping config write.");
            return;
        }

        var root = HostFolder;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "xmltv"));

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var channels = await db.Channels.AsNoTracking()
            .Where(c => c.Enabled)
            .OrderBy(c => c.Number)
            .ToListAsync(cancellationToken);

        var entries = new List<NextChannelEntry>();
        foreach (var channel in channels)
        {
            var number = ChannelNumbers.Format(channel.Number);
            var channelFolder = Path.Combine(root, "channels", number);
            Directory.CreateDirectory(channelFolder);

            var channelConfig = BuildChannelConfig(channel);
            await File.WriteAllTextAsync(
                Path.Combine(channelFolder, "channel.json"),
                FinTvJson.Serialize(channelConfig),
                cancellationToken);

            await WritePlayoutWindowsAsync(channel, number, next, cancellationToken);

            entries.Add(new NextChannelEntry
            {
                Number = number,
                Name = channel.Name,
                Config = $"./channels/{number}/channel.json",
                TvgId = channel.Id.ToString("N"),
            });
        }

        var lineup = new NextLineupConfig
        {
            Server = new NextServerConfig(),
            Output = new NextOutputConfig { Folder = next.HlsOutputFolder },
            Xmltv = new NextXmltvConfig { Folder = $"{next.NextConfigFolder.TrimEnd('/')}/xmltv" },
            Channels = entries,
        };

        await File.WriteAllTextAsync(
            Path.Combine(root, "lineup.json"),
            FinTvJson.Serialize(lineup),
            cancellationToken);

        _logger.LogInformation(
            "Wrote ErsatzTV next configuration for {Count} channels to {Folder}",
            channels.Count,
            root);
    }

    /// <summary>
    /// Removes the generated next configuration folder (call when next is disabled).
    /// </summary>
    public void ClearConfiguration()
    {
        var folder = HostFolder;
        if (Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not clear next configuration folder {Folder}", folder);
            }
        }
    }

    private NextChannelConfig BuildChannelConfig(Channel channel)
    {
        var normalization = FinTvRuntime.Current?.Configuration.Normalization ?? new NormalizationSettings();
        var transcode = FinTvRuntime.Current?.Configuration.Transcode ?? new TranscodeSettings();
        var (width, height) = ParseResolution(normalization.Resolution);

        // Mirror ChannelFlow's encoder: saved Transcode settings win, otherwise fall
        // back to the FFMPEG_* environment (VAAPI/QSV/NVENC) so next uses the same GPU.
        var effectiveAccel = FfmpegEncodingService.NormalizeAcceleration(
            string.IsNullOrWhiteSpace(transcode.HardwareAcceleration)
                ? _encoding.EnvironmentHardwareAcceleration
                : transcode.HardwareAcceleration,
            string.IsNullOrWhiteSpace(transcode.VideoEncoder)
                ? _encoding.EnvironmentVideoEncoder
                : transcode.VideoEncoder);
        var accel = MapHardwareAcceleration(effectiveAccel);
        var vaapiDevice = string.IsNullOrWhiteSpace(transcode.VaapiDevice)
            ? _encoding.EnvironmentVaapiDevice
            : transcode.VaapiDevice;
        var copyMode = normalization.NormalizationMode.Equals("copy", StringComparison.OrdinalIgnoreCase);

        return new NextChannelConfig
        {
            Playout = new NextPlayoutConfig { Folder = "./playout" },
            Ffmpeg = new NextFfmpegConfig(),
            Normalization = new NextNormalizationConfig
            {
                Audio = new NextAudioNormalization
                {
                    Mode = copyMode ? "copy" : "transcode",
                    CopyFormats = copyMode ? NextCopyFormats.Audio : null,
                    Format = MapAudioCodec(normalization.AudioCodec),
                    BitrateKbps = ParseBitrate(normalization.AudioBitrate),
                    BufferKbps = ParseBitrate(normalization.AudioBitrate) is int ab ? ab : null,
                    Channels = ParseChannels(normalization.AudioChannels),
                    SampleRateHz = ParseInt(normalization.AudioSampleRate),
                    NormalizeLoudness = true,
                },
                Video = new NextVideoNormalization
                {
                    Mode = copyMode ? "copy" : "transcode",
                    CopyFormats = copyMode ? NextCopyFormats.Video : null,
                    Format = MapVideoCodec(normalization.VideoCodec),
                    Width = width,
                    Height = height,
                    BitrateKbps = ParseBitrate(normalization.VideoBitrate),
                    BufferKbps = ParseBitrate(normalization.VideoBitrate) is int v ? Math.Max(v * 2, 0) : null,
                    Profile = MapH264Profile(normalization.VideoProfile, normalization.VideoCodec),
                    Accel = accel,
                    VaapiDevice = accel is "vaapi" or "qsv" ? vaapiDevice : null,
                    FrameRate = ParseFrameRate(normalization.FrameRate),
                },
                Subtitle = new NextSubtitleNormalization { Mode = "burn" },
            },
        };
    }

    private async Task WritePlayoutWindowsAsync(
        Channel channel,
        string number,
        NextTranscodingSettings next,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(HostFolder, "channels", number, "playout");
        Directory.CreateDirectory(folder);

        var tz = ScheduleTimeZoneHelper.ResolveScheduleTimeZone(_logger);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var startOfToday = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, DateTimeKind.Unspecified);

        var resolverUri = $"{next.ResolverBaseUrl!.TrimEnd('/')}/iptv/next/resolve/{channel.Id:N}" +
            $"?token={Uri.EscapeDataString(next.ResolverToken!)}";

        // Two non-overlapping 24-hour windows ([prior day, today] and [today, tomorrow])
        // guarantee a window always covers "now", including across midnight.
        var dayStarts = new[] { startOfToday.AddDays(-1), startOfToday };
        foreach (var dayStart in dayStarts)
        {
            var start = ToOffset(dayStart, tz);
            var finish = ToOffset(dayStart.AddDays(1), tz);

            var playout = new NextPlayout
            {
                Items =
                [
                    new NextPlayoutItem
                    {
                        Id = "d",
                        Start = start.ToString("o", CultureInfo.InvariantCulture),
                        Finish = finish.ToString("o", CultureInfo.InvariantCulture),
                        Source = new NextDynamicSource { Uri = resolverUri },
                    },
                ],
            };

            await File.WriteAllTextAsync(
                Path.Combine(folder, NextPlayoutFilename.ForWindow(start, finish)),
                FinTvJson.Serialize(playout),
                cancellationToken);
        }
    }

    private static DateTimeOffset ToOffset(DateTime localDateTime, TimeZoneInfo tz)
    {
        var specified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(specified, tz.GetUtcOffset(specified));
    }

    private static (int? Width, int? Height) ParseResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution) || resolution.Equals("match", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var parts = resolution.Split('x', 'X', StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            && width > 0 && height > 0)
        {
            return (width, height);
        }

        return (null, null);
    }

    private static int? ParseBitrate(string? bitrate)
    {
        if (string.IsNullOrWhiteSpace(bitrate) || bitrate.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var trimmed = bitrate.Trim().ToLowerInvariant();
        var multiplier = 1;
        if (trimmed.EndsWith("k", StringComparison.Ordinal))
        {
            multiplier = 1000;
            trimmed = trimmed[..^1];
        }
        else if (trimmed.EndsWith("m", StringComparison.Ordinal))
        {
            multiplier = 1_000_000;
            trimmed = trimmed[..^1];
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            return Math.Max(1, (int)Math.Round(value * multiplier / 1000.0));
        }

        return null;
    }

    private static int? ParseChannels(string? channels)
    {
        if (string.IsNullOrWhiteSpace(channels))
        {
            return null;
        }

        var trimmed = channels.Trim();
        if (trimmed == "2.0")
        {
            return 2;
        }

        if (trimmed == "5.1")
        {
            return 6;
        }

        if (trimmed == "7.1")
        {
            return 8;
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0)
        {
            return count;
        }

        return null;
    }

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;

    private static string? ParseFrameRate(string? frameRate)
    {
        if (string.IsNullOrWhiteSpace(frameRate) || frameRate.Equals("match", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return frameRate.Trim();
    }

    private static string? MapVideoCodec(string? codec)
        => codec?.Trim().ToLowerInvariant() switch
        {
            "hevc" or "h265" => "hevc",
            "mpeg2" or "mpeg2video" => "mpeg2video",
            _ => "h264",
        };

    private static string? MapH264Profile(string? profile, string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec) || codec.Trim().ToLowerInvariant() != "h264")
        {
            return null;
        }

        var p = profile?.Trim().ToLowerInvariant();
        return p is "main" or "high" or "baseline" ? p : "main";
    }

    private static string MapAudioCodec(string? codec)
        => codec?.Trim().ToLowerInvariant() switch
        {
            "ac3" => "ac3",
            _ => "aac",
        };

    private static string? MapHardwareAcceleration(string? acceleration)
        => acceleration?.Trim().ToLowerInvariant() switch
        {
            "vaapi" => "vaapi",
            "qsv" => "qsv",
            "nvenc" => "cuda",
            "amf" => "amf",
            "videotoolbox" => "videotoolbox",
            _ => null,
        };
}

/// <summary>
/// Codecs that mux cleanly into HLS MPEG-TS. When Normalization mode is <c>copy</c>,
/// next stream-copies sources whose codecs appear in these lists and transcodes the rest.
/// Mirrors next's VideoCopyFormat / AudioCopyFormat enums.
/// </summary>
internal static class NextCopyFormats
{
    public static readonly List<string> Video = ["h264", "hevc", "mpeg2video"];

    public static readonly List<string> Audio = ["aac", "ac3", "eac3", "mp2", "mp3"];
}
