using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using FinTv.Configuration;
using FinTv.Domain;

namespace FinTv.Streaming;

/// <summary>
/// Probes VAAPI/QSV/NVENC encode support and per-device VAAPI decode profiles (vainfo VLD).
/// </summary>
public sealed class GpuCapabilityService
{
    private static readonly GpuSelectOption[] AllResolutions =
    [
        new("match", "Match channel (1080p 16:9 or 4:3)"),
        new("480p", "480p"),
        new("720p", "720p"),
        new("1080p", "1080p")
    ];

    private static readonly GpuSelectOption[] AllFrameRates =
    [
        new("23.976", "23.976"),
        new("24", "24"),
        new("25", "25"),
        new("29.97", "29.97"),
        new("30", "30"),
        new("50", "50"),
        new("59.94", "59.94"),
        new("60", "60")
    ];

    private static readonly GpuSelectOption[] AllH264Profiles =
    [
        new("baseline", "Baseline"),
        new("main", "Main"),
        new("high", "High")
    ];

    private static readonly GpuSelectOption[] AllVideoCodecs =
    [
        new("h264", "H.264"),
        new("mpeg2", "MPEG-2")
    ];

    private static readonly string[] HighFrameRates = ["50", "59.94", "60"];

    private readonly IFfmpegLocator _ffmpeg;
    private readonly ILogger<GpuCapabilityService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GpuCapabilities? _cached;
    private int _emptyHardwareProbes;
    private readonly HashSet<string> _encodeVaapiDevices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _encodeQsvDevices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _vaapiDecodeCodecs = new(StringComparer.Ordinal);

    public GpuCapabilityService(IFfmpegLocator ffmpeg, ILogger<GpuCapabilityService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public GpuCapabilities? TryGetCached() => _cached;

    public void Invalidate()
    {
        _cached = null;
        _encodeVaapiDevices.Clear();
        _encodeQsvDevices.Clear();
        _vaapiDecodeCodecs.Clear();
    }

    public async Task<GpuCapabilities> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null && HasHardwareEncode(_cached))
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && !ShouldRetryEmptyHardwareProbe())
            {
                return _cached;
            }

            _cached = await ProbeAsync(cancellationToken);
            if (ShouldRetryEmptyHardwareProbe())
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                _cached = await ProbeAsync(cancellationToken);
            }

            if (HasHardwareEncode(_cached))
            {
                _emptyHardwareProbes = 0;
            }
            else if (DiscoverVaapiDevices().Any())
            {
                _emptyHardwareProbes++;
            }

            _logger.LogInformation("GPU encode capabilities: {Summary}", _cached.Summary);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public GpuFormatLimits FormatFor(string? acceleration)
    {
        var caps = _cached ?? GpuCapabilities.SoftwareOnly;
        var key = FfmpegEncodingService.NormalizeAcceleration(acceleration, null);
        return caps.Formats.TryGetValue(key, out var format) ? format : caps.Formats["none"];
    }

    public string ClampAcceleration(string? acceleration, string? encoder)
    {
        var requested = FfmpegEncodingService.NormalizeAcceleration(acceleration, encoder);
        if (requested is "none" or "vaapi" or "qsv" or "nvenc")
        {
            return requested;
        }

        var caps = _cached;
        if (caps is null)
        {
            return "none";
        }

        return caps.Accelerations.Any(item => item.Id == requested)
            ? requested
            : "none";
    }

    public string ClampEncoder(string? encoder, string acceleration)
    {
        if (string.IsNullOrWhiteSpace(encoder) || encoder.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        var trimmed = encoder.Trim();
        var caps = _cached;
        if (caps is null)
        {
            return trimmed;
        }

        var accel = caps.Accelerations.FirstOrDefault(item => item.Id == acceleration);
        if (accel is null)
        {
            return "auto";
        }

        return accel.Encoders.Any(item => item.Value.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            ? trimmed
            : "auto";
    }

    public string ClampVaapiDevice(string? device)
    {
        var trimmed = string.IsNullOrWhiteSpace(device) ? null : device.Trim();
        if (IsSelectableRenderNode(trimmed))
        {
            return trimmed!;
        }

        var caps = _cached;
        var preferred = caps?.VaapiDevices.FirstOrDefault(item =>
                CanEncodeOnVaapiDevice(item.Value) || CanEncodeOnQsvDevice(item.Value))
            ?? caps?.VaapiDevices.FirstOrDefault();
        if (preferred is not null)
        {
            return preferred.Value;
        }

        return DiscoverVaapiDevices().FirstOrDefault()
            ?? trimmed
            ?? "/dev/dri/renderD128";
    }

    public bool CanEncodeOnVaapiDevice(string? device)
    {
        if (string.IsNullOrWhiteSpace(device))
        {
            return false;
        }

        if (_encodeVaapiDevices.Contains(device))
        {
            return true;
        }

        return _cached is null && File.Exists(device);
    }

    public bool CanEncodeOnQsvDevice(string? device)
    {
        if (string.IsNullOrWhiteSpace(device))
        {
            return false;
        }

        if (_encodeQsvDevices.Contains(device))
        {
            return true;
        }

        return _cached is null && File.Exists(device);
    }

    /// <summary>
    /// True when this render node reports a VAAPI VLD profile for the source codec,
    /// or when vainfo did not return decode data (unknown → keep current hwaccel).
    /// AV1 is never hardware-decoded; Intel iHD advertises it and then SIGSEGVs.
    /// </summary>
    public bool CanVaapiDecode(string? device, string? sourceVideoCodec)
    {
        var codec = FfmpegEncodingService.NormalizeVideoCodec(sourceVideoCodec);
        if (codec is null)
        {
            return true;
        }

        if (FfmpegEncodingService.IsUnsafeVaapiDecodeCodec(codec))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(device)
            || !_vaapiDecodeCodecs.TryGetValue(device, out var codecs)
            || codecs.Count == 0)
        {
            return true;
        }

        return codecs.Contains(codec);
    }

    /// <summary>
    /// QSV decode uses MediaSDK/oneVPL (<c>av1_qsv</c>, <c>h264_qsv</c>, …), not
    /// VAAPI <c>-hwaccel vaapi</c>. AV1 is allowed when vainfo lists VLD.
    /// </summary>
    public bool CanQsvDecode(string? device, string? sourceVideoCodec)
    {
        var codec = FfmpegEncodingService.NormalizeVideoCodec(sourceVideoCodec);
        if (codec is null)
        {
            return true;
        }

        if (FfmpegEncodingService.QsvDecoderFor(codec) is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(device)
            || !_vaapiDecodeCodecs.TryGetValue(device, out var codecs)
            || codecs.Count == 0)
        {
            return true;
        }

        return codecs.Contains(codec);
    }

    private bool IsSelectableRenderNode(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (_cached?.VaapiDevices.Any(item => item.Value == path) == true)
        {
            return true;
        }

        return path.StartsWith("/dev/dri/", StringComparison.Ordinal)
            && File.Exists(path);
    }

    private bool ShouldRetryEmptyHardwareProbe()
        => _emptyHardwareProbes < 3
           && (_cached is null || !HasHardwareEncode(_cached))
           && DiscoverVaapiDevices().Any();

    private static bool HasHardwareEncode(GpuCapabilities caps)
        => caps.Accelerations.Any(item => item.Id is "vaapi" or "qsv" or "nvenc");

    public NormalizationSettings ClampNormalization(NormalizationSettings settings, string acceleration)
    {
        var format = FormatFor(acceleration);
        var codec = Pick(settings.VideoCodec, "h264", format.VideoCodecs.Select(item => item.Value));
        var profile = codec == "mpeg2"
            ? "main"
            : Pick(settings.VideoProfile, "main", format.H264Profiles.Select(item => item.Value));
        var resolution = Pick(settings.Resolution, "match", format.Resolutions.Select(item => item.Value));
        var frameRate = Pick(settings.FrameRate, "30", format.FrameRates.Select(item => item.Value));
        return new NormalizationSettings
        {
            Resolution = resolution,
            FrameRate = frameRate,
            VideoCodec = codec,
            VideoProfile = profile,
            VideoBitrate = settings.VideoBitrate,
            AudioCodec = settings.AudioCodec,
            AudioChannels = settings.AudioChannels,
            AudioSampleRate = settings.AudioSampleRate,
            AudioBitrate = settings.AudioBitrate,
            NormalizationMode = settings.NormalizationMode
        };
    }

    public string MapVaapiH264Profile(string? uiProfile)
    {
        var format = FormatFor("vaapi");
        var allowed = format.H264Profiles.Select(item => item.Value).ToArray();
        var chosen = Pick(uiProfile, "main", allowed);
        return chosen == "baseline" ? "constrained_baseline" : chosen;
    }

    public bool SupportsMpeg2Vaapi()
        => FormatFor("vaapi").VideoCodecs.Any(item => item.Value == "mpeg2");

    public bool SupportsMpeg2Qsv()
        => FormatFor("qsv").VideoCodecs.Any(item => item.Value == "mpeg2");

    private async Task<GpuCapabilities> ProbeAsync(CancellationToken cancellationToken)
    {
        var encoders = await ReadEncoderNamesAsync(cancellationToken);
        var hasLibx264 = encoders.Contains("libx264");
        var hasMpeg2Video = encoders.Contains("mpeg2video");
        var hasH264Vaapi = encoders.Contains("h264_vaapi");
        var hasMpeg2Vaapi = encoders.Contains("mpeg2_vaapi");
        var hasH264Qsv = encoders.Contains("h264_qsv");
        var hasMpeg2Qsv = encoders.Contains("mpeg2_qsv");
        var hasH264Nvenc = encoders.Contains("h264_nvenc");
        _logger.LogInformation(
            "ffmpeg at {Path} listed {Count} encoders (h264_vaapi={Vaapi}, h264_qsv={Qsv}, mpeg2_vaapi={Mpeg2}, h264_nvenc={Nvenc}, libx264={X264})",
            _ffmpeg.EncoderPath,
            encoders.Count,
            hasH264Vaapi,
            hasH264Qsv,
            hasMpeg2Vaapi,
            hasH264Nvenc,
            hasLibx264);

        var software = new GpuFormatLimits(
            Filter(AllVideoCodecs, hasMpeg2Video ? ["h264", "mpeg2"] : ["h264"]),
            AllH264Profiles,
            AllResolutions,
            AllFrameRates);

        var accelerations = new List<GpuAccelOption>
        {
            new(
                "none",
                "Software",
                true,
                FilterEncoders(["auto", hasLibx264 ? "libx264" : null]),
                [])
        };
        var formats = new Dictionary<string, GpuFormatLimits>(StringComparer.OrdinalIgnoreCase)
        {
            ["none"] = software
        };

        var vaapiDevices = new List<GpuSelectOption>();
        var driverNotes = new List<string>();
        _encodeVaapiDevices.Clear();
        _encodeQsvDevices.Clear();
        _vaapiDecodeCodecs.Clear();

        var discovered = DiscoverVaapiDevices().ToList();
        if (hasH264Vaapi || hasH264Qsv || discovered.Count > 0)
        {
            foreach (var path in discovered)
            {
                var vainfo = await ReadVainfoAsync(path, cancellationToken);
                if (vainfo is { DecodeCodecs.Count: > 0 })
                {
                    _vaapiDecodeCodecs[path] = vainfo.DecodeCodecs;
                    _logger.LogInformation(
                        "VAAPI decode on {Device}: {Codecs}",
                        path,
                        string.Join(", ", vainfo.DecodeCodecs.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)));
                }

                var vaapi = await ProbeVaapiDeviceAsync(path, hasMpeg2Vaapi, vainfo, cancellationToken);
                if (vaapi is not null)
                {
                    _encodeVaapiDevices.Add(path);
                    vaapiDevices.Add(new GpuSelectOption(path, vaapi.Label));
                    driverNotes.Add(vaapi.Driver);
                    formats["vaapi"] = vaapi.Format;
                }
                else
                {
                    vaapiDevices.Add(new GpuSelectOption(
                        path,
                        string.IsNullOrWhiteSpace(vainfo?.Driver)
                            ? $"{path} (no H.264 VAAPI encode)"
                            : $"{path} ({vainfo.Driver}, no H.264 VAAPI encode)"));
                }

                var qsv = await ProbeQsvDeviceAsync(path, hasMpeg2Qsv, cancellationToken);
                if (qsv is not null)
                {
                    _encodeQsvDevices.Add(path);
                    formats["qsv"] = qsv;
                    _logger.LogInformation("QSV H.264 encode on {Device}", path);
                }
            }

            if (vaapiDevices.Count > 0 && (_encodeVaapiDevices.Count > 0 || hasH264Vaapi))
            {
                if (!formats.ContainsKey("vaapi"))
                {
                    formats["vaapi"] = software;
                }

                accelerations.Add(new GpuAccelOption(
                    "vaapi",
                    "Intel / AMD VAAPI",
                    true,
                    FilterEncoders(["auto", "h264_vaapi"]),
                    vaapiDevices));
            }
            else if (discovered.Count > 0 && !hasH264Vaapi && _encodeVaapiDevices.Count == 0)
            {
                _logger.LogWarning(
                    "Render nodes exist but ffmpeg did not list h264_vaapi and no device encoded a test frame. Encoder={Path}",
                    _ffmpeg.EncoderPath);
            }

            if (discovered.Count > 0 || hasH264Qsv)
            {
                if (!formats.ContainsKey("qsv"))
                {
                    formats["qsv"] = formats.GetValueOrDefault("vaapi") ?? software;
                }

                accelerations.Add(new GpuAccelOption(
                    "qsv",
                    "Intel Quick Sync (QSV)",
                    true,
                    FilterEncoders(["auto", "h264_qsv"]),
                    vaapiDevices));
            }

            if (_encodeQsvDevices.Count == 0 && discovered.Count > 0)
            {
                _logger.LogInformation(
                    "QSV is listed for Transcode even though no device encoded a test frame (h264_qsv listed={Listed}). Encoder={Path}",
                    hasH264Qsv,
                    _ffmpeg.EncoderPath);
            }
        }

        if (hasH264Nvenc)
        {
            var nvenc = await ProbeNvencAsync(cancellationToken);
            if (nvenc is not null)
            {
                driverNotes.Add(nvenc.Driver);
                formats["nvenc"] = nvenc.Format;
                accelerations.Add(new GpuAccelOption(
                    "nvenc",
                    "NVIDIA NVENC",
                    true,
                    FilterEncoders(["auto", "h264_nvenc"]),
                    []));
            }
        }

        var summary = BuildSummary(accelerations, formats, driverNotes, vaapiDevices);
        return new GpuCapabilities(
            summary,
            string.Join("; ", driverNotes.Distinct()),
            vaapiDevices,
            accelerations,
            formats,
            SnapshotDecodeCodecs());
    }

    private async Task<VaapiProbe?> ProbeVaapiDeviceAsync(
        string device,
        bool hasMpeg2Vaapi,
        VainfoResult? vainfo,
        CancellationToken cancellationToken)
    {
        vainfo ??= await ReadVainfoAsync(device, cancellationToken);
        var h264 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mpeg2 = false;
        if (vainfo is not null)
        {
            foreach (var profile in vainfo.EncodeProfiles)
            {
                if (profile.Contains("H264ConstrainedBaseline", StringComparison.OrdinalIgnoreCase)
                    || profile.Contains("H264Baseline", StringComparison.OrdinalIgnoreCase))
                {
                    h264.Add("baseline");
                }
                else if (profile.Contains("H264Main", StringComparison.OrdinalIgnoreCase))
                {
                    h264.Add("main");
                }
                else if (profile.Contains("H264High", StringComparison.OrdinalIgnoreCase)
                         && !profile.Contains("High10", StringComparison.OrdinalIgnoreCase)
                         && !profile.Contains("High422", StringComparison.OrdinalIgnoreCase)
                         && !profile.Contains("High444", StringComparison.OrdinalIgnoreCase))
                {
                    h264.Add("high");
                }
                else if (profile.Contains("MPEG2", StringComparison.OrdinalIgnoreCase))
                {
                    mpeg2 = true;
                }
            }
        }

        if (h264.Count == 0)
        {
            h264.UnionWith(["baseline", "main", "high"]);
        }

        var deviceArgs = HardwareVaapiArgs(device);
        // Live encode omits -profile:v (Intel iHD rejects an explicit Main/High on
        // real surfaces). Probe the same way first so a working GPU is not marked
        // software-only.
        var canEncode = await CanEncodeAsync(deviceArgs, "h264_vaapi", null, 640, 360, "30", cancellationToken);
        var confirmed = new List<string>();
        foreach (var profile in AllH264Profiles.Select(item => item.Value))
        {
            if (!h264.Contains(profile))
            {
                continue;
            }

            var ffmpegProfile = profile == "baseline" ? "constrained_baseline" : profile;
            if (await CanEncodeAsync(deviceArgs, "h264_vaapi", ffmpegProfile, 640, 360, "30", cancellationToken))
            {
                confirmed.Add(profile);
            }
        }

        if (!canEncode && confirmed.Count == 0)
        {
            _logger.LogWarning("VAAPI device {Device} has no usable H.264 encode", device);
            return null;
        }

        if (confirmed.Count == 0)
        {
            confirmed.AddRange(h264.Contains("main") ? ["main"] : h264);
        }

        var allow1080 = await CanEncodeAsync(deviceArgs, "h264_vaapi", null, 1920, 1080, "30", cancellationToken);
        var allow720 = allow1080 || await CanEncodeAsync(deviceArgs, "h264_vaapi", null, 1280, 720, "30", cancellationToken);
        var allow60 = allow1080 && await CanEncodeAsync(deviceArgs, "h264_vaapi", null, 1920, 1080, "60", cancellationToken);

        if (mpeg2 && hasMpeg2Vaapi)
        {
            mpeg2 = await CanEncodeAsync(
                deviceArgs, "mpeg2_vaapi", null, 640, 360, "30", cancellationToken);
        }
        else
        {
            mpeg2 = false;
        }

        var driver = vainfo?.Driver ?? "VAAPI";
        var format = new GpuFormatLimits(
            Filter(AllVideoCodecs, mpeg2 ? ["h264", "mpeg2"] : ["h264"]),
            Filter(AllH264Profiles, confirmed),
            Filter(AllResolutions, allow1080 ? ["match", "480p", "720p", "1080p"]
                : allow720 ? ["480p", "720p"]
                : ["480p"]),
            Filter(AllFrameRates, allow60 ? AllFrameRates.Select(item => item.Value) : AllFrameRates.Select(item => item.Value).Where(value => !HighFrameRates.Contains(value))));

        return new VaapiProbe(driver, $"{device} ({driver})", format);
    }

    private async Task<GpuFormatLimits?> ProbeQsvDeviceAsync(
        string device,
        bool hasMpeg2Qsv,
        CancellationToken cancellationToken)
    {
        var deviceArgs = HardwareQsvArgs(device);
        if (!await CanEncodeAsync(deviceArgs, "h264_qsv", null, 640, 360, "30", cancellationToken))
        {
            return null;
        }

        var allow1080 = await CanEncodeAsync(deviceArgs, "h264_qsv", null, 1920, 1080, "30", cancellationToken);
        var allow720 = allow1080
            || await CanEncodeAsync(deviceArgs, "h264_qsv", null, 1280, 720, "30", cancellationToken);
        var allow60 = allow1080
            && await CanEncodeAsync(deviceArgs, "h264_qsv", null, 1920, 1080, "60", cancellationToken);
        var mpeg2 = hasMpeg2Qsv
            && await CanEncodeAsync(deviceArgs, "mpeg2_qsv", null, 640, 360, "30", cancellationToken);

        return new GpuFormatLimits(
            Filter(AllVideoCodecs, mpeg2 ? ["h264", "mpeg2"] : ["h264"]),
            AllH264Profiles,
            Filter(AllResolutions, allow1080 ? ["match", "480p", "720p", "1080p"]
                : allow720 ? ["480p", "720p"]
                : ["480p"]),
            Filter(
                AllFrameRates,
                allow60
                    ? AllFrameRates.Select(item => item.Value)
                    : AllFrameRates.Select(item => item.Value).Where(value => !HighFrameRates.Contains(value))));
    }

    private async Task<NvencProbe?> ProbeNvencAsync(CancellationToken cancellationToken)
    {
        var gpuName = await ReadNvidiaNameAsync(cancellationToken) ?? "NVIDIA";
        var confirmed = new List<string>();
        foreach (var profile in AllH264Profiles.Select(item => item.Value))
        {
            if (await CanEncodeAsync([], "h264_nvenc", profile, 640, 360, "30", cancellationToken))
            {
                confirmed.Add(profile);
            }
        }

        if (confirmed.Count == 0)
        {
            return null;
        }

        var working = confirmed.Contains("main") ? "main" : confirmed[0];
        var allow1080 = await CanEncodeAsync([], "h264_nvenc", working, 1920, 1080, "30", cancellationToken);
        var allow720 = allow1080 || await CanEncodeAsync([], "h264_nvenc", working, 1280, 720, "30", cancellationToken);
        var allow60 = allow1080 && await CanEncodeAsync([], "h264_nvenc", working, 1920, 1080, "60", cancellationToken);
        var format = new GpuFormatLimits(
            Filter(AllVideoCodecs, ["h264"]),
            Filter(AllH264Profiles, confirmed),
            Filter(AllResolutions, allow1080 ? ["match", "480p", "720p", "1080p"]
                : allow720 ? ["480p", "720p"]
                : ["480p"]),
            Filter(AllFrameRates, allow60 ? AllFrameRates.Select(item => item.Value) : AllFrameRates.Select(item => item.Value).Where(value => !HighFrameRates.Contains(value))));
        return new NvencProbe(gpuName, format);
    }

    private static IReadOnlyList<string> HardwareVaapiArgs(string device)
        => ["-init_hw_device", $"vaapi=va:{device}", "-filter_hw_device", "va"];

    private static IReadOnlyList<string> HardwareQsvArgs(string device)
        =>
        [
            "-init_hw_device", $"vaapi=va:{device}",
            "-init_hw_device", "qsv=hw@va",
            "-filter_hw_device", "hw"
        ];

    private async Task<bool> CanEncodeAsync(
        IReadOnlyList<string> deviceArgs,
        string encoder,
        string? profile,
        int width,
        int height,
        string frameRate,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        args.AddRange(deviceArgs);
        args.AddRange(
        [
            "-f", "lavfi",
            "-i", $"color=c=black:s={width}x{height}:r={frameRate}:d=0.2"
        ]);
        if (encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-vf", "format=nv12,hwupload=extra_hw_frames=64"]);
        }
        else if (encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-vf", "format=nv12,hwupload=extra_hw_frames=64,format=qsv"]);
        }
        else if (encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-vf", "format=yuv420p"]);
        }

        args.AddRange(["-c:v", encoder]);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            args.AddRange(["-profile:v", profile]);
        }

        args.AddRange(["-b:v", "1000k", "-an", "-f", "null", "-"]);
        var (ok, stderr) = await RunAsync(_ffmpeg.EncoderPath, args, TimeSpan.FromSeconds(8), cancellationToken);
        if (!ok)
        {
            if (profile is null)
            {
                _logger.LogWarning(
                    "Encode probe failed encoder={Encoder} {Width}x{Height}@{Rate}: {Error}",
                    encoder,
                    width,
                    height,
                    frameRate,
                    TrimError(stderr));
            }
            else
            {
                _logger.LogDebug(
                    "Encode probe failed encoder={Encoder} profile={Profile} {Width}x{Height}@{Rate}: {Error}",
                    encoder,
                    profile,
                    width,
                    height,
                    frameRate,
                    TrimError(stderr));
            }
        }

        return ok;
    }

    private async Task<HashSet<string>> ReadEncoderNamesAsync(CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (_, text) = await RunAsync(
            _ffmpeg.EncoderPath,
            ["-hide_banner", "-loglevel", "error", "-encoders"],
            TimeSpan.FromSeconds(8),
            cancellationToken);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\s*[A-Z\.]+\s+([A-Za-z0-9_]+)");
            if (match.Success)
            {
                names.Add(match.Groups[1].Value);
            }
        }

        foreach (var known in new[] { "h264_vaapi", "mpeg2_vaapi", "h264_qsv", "mpeg2_qsv", "h264_nvenc", "libx264", "mpeg2video" })
        {
            if (text.Contains(known, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(known);
            }
        }

        return names;
    }

    private static IEnumerable<string> DiscoverVaapiDevices()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            if (Directory.Exists("/dev/dri"))
            {
                foreach (var path in Directory.GetFiles("/dev/dri", "renderD*"))
                {
                    found.Add(path);
                }
            }
        }
        catch (Exception)
        {
            // Device nodes can be missing in some containers.
        }

        return found;
    }

    private async Task<VainfoResult?> ReadVainfoAsync(string device, CancellationToken cancellationToken)
    {
        var path = FindOnPath("vainfo");
        if (path is null)
        {
            return null;
        }

        var (ok, text) = await RunAsync(
            path,
            ["--display", "drm", "--device", device],
            TimeSpan.FromSeconds(8),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var driver = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("vainfo: Driver version:", StringComparison.OrdinalIgnoreCase))
            ?.Split(':', 2) is { Length: 2 } parts
            ? parts[1].Trim()
            : ok ? "VAAPI" : null;
        if (driver is null)
        {
            return null;
        }

        var encode = new List<string>();
        var decode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var isEncode = trimmed.Contains("VAEntrypointEnc", StringComparison.OrdinalIgnoreCase);
            var isDecode = trimmed.Contains("VAEntrypointVLD", StringComparison.OrdinalIgnoreCase);
            if (!isEncode && !isDecode)
            {
                continue;
            }

            var profile = trimmed.Split(':', 2)[0].Trim();
            if (string.IsNullOrWhiteSpace(profile))
            {
                continue;
            }

            if (isEncode)
            {
                encode.Add(profile);
            }

            if (isDecode)
            {
                var codec = MapVaapiProfileToCodec(profile);
                if (!string.IsNullOrWhiteSpace(codec))
                {
                    decode.Add(codec);
                }
            }
        }

        return new VainfoResult(driver, encode, decode);
    }

    private static async Task<string?> ReadNvidiaNameAsync(CancellationToken cancellationToken)
    {
        var path = FindOnPath("nvidia-smi");
        if (path is null)
        {
            return File.Exists("/dev/nvidia0") ? "NVIDIA" : null;
        }

        var (ok, text) = await RunAsync(
            path,
            ["--query-gpu=name", "--format=csv,noheader"],
            TimeSpan.FromSeconds(6),
            cancellationToken);
        if (!ok)
        {
            return File.Exists("/dev/nvidia0") ? "NVIDIA" : null;
        }

        var name = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(name) ? "NVIDIA" : name;
    }

    private static async Task<(bool Ok, string Text)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return (false, string.Empty);
        }

        try
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var result = await Cli.Wrap(fileName)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                .ExecuteAsync(timeoutCts.Token);
            var text = string.Concat(stdout, Environment.NewLine, stderr);
            return (result.ExitCode == 0, text);
        }
        catch (Exception)
        {
            return (false, string.Empty);
        }
    }

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var wellKnown in new[] { $"/usr/bin/{name}", $"/usr/local/bin/{name}" })
        {
            if (File.Exists(wellKnown))
            {
                return wellKnown;
            }
        }

        return null;
    }

    private static IReadOnlyList<GpuSelectOption> Filter(IReadOnlyList<GpuSelectOption> all, IEnumerable<string> allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        return all.Where(item => set.Contains(item.Value)).ToArray();
    }

    private static IReadOnlyList<GpuSelectOption> FilterEncoders(IEnumerable<string?> names)
        => names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new GpuSelectOption(name!, name == "auto" ? "Auto (match acceleration)" : name!))
            .ToArray();

    private static string Pick(string? value, string fallback, IEnumerable<string> allowed)
    {
        var list = allowed.ToArray();
        if (list.Length == 0)
        {
            return fallback;
        }

        if (!string.IsNullOrWhiteSpace(value)
            && list.Any(item => item.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return list.First(item => item.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (list.Any(item => item.Equals(fallback, StringComparison.OrdinalIgnoreCase)))
        {
            return fallback;
        }

        return list[0];
    }

    private static string BuildSummary(
        IReadOnlyList<GpuAccelOption> accelerations,
        IReadOnlyDictionary<string, GpuFormatLimits> formats,
        IReadOnlyList<string> drivers,
        IReadOnlyList<GpuSelectOption> devices)
    {
        var hw = accelerations.Where(item => item.Id != "none").Select(item => item.Label).ToArray();
        if (hw.Length == 0)
        {
            return "No hardware encoder found. Software libx264 can use every Normalization option.";
        }

        var parts = new List<string>();
        if (drivers.Count > 0)
        {
            parts.Add(drivers[0]);
        }

        if (devices.Count > 0)
        {
            parts.Add(devices[0].Value);
        }

        if (formats.TryGetValue("vaapi", out var vaapi))
        {
            parts.Add("H.264 " + string.Join("/", vaapi.H264Profiles.Select(item => item.Label)));
            if (vaapi.VideoCodecs.Any(item => item.Value == "mpeg2"))
            {
                parts.Add("MPEG-2");
            }

            parts.Add(vaapi.Resolutions.Any(item => item.Value == "1080p") ? "up to 1080p" : "up to 720p");
            parts.Add(vaapi.FrameRates.Any(item => item.Value == "60") ? "60 fps" : "30 fps");
            if (formats.ContainsKey("qsv"))
            {
                parts.Add("QSV");
            }
        }
        else if (formats.TryGetValue("qsv", out var qsv))
        {
            parts.Add("QSV H.264 " + string.Join("/", qsv.H264Profiles.Select(item => item.Label)));
            if (qsv.VideoCodecs.Any(item => item.Value == "mpeg2"))
            {
                parts.Add("MPEG-2");
            }

            parts.Add(qsv.Resolutions.Any(item => item.Value == "1080p") ? "up to 1080p" : "up to 720p");
            parts.Add(qsv.FrameRates.Any(item => item.Value == "60") ? "60 fps" : "30 fps");
        }
        else if (formats.TryGetValue("nvenc", out var nvenc))
        {
            parts.Add("H.264 " + string.Join("/", nvenc.H264Profiles.Select(item => item.Label)));
            parts.Add(nvenc.Resolutions.Any(item => item.Value == "1080p") ? "up to 1080p" : "up to 720p");
        }

        return string.Join(" · ", parts);
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> SnapshotDecodeCodecs()
        => _vaapiDecodeCodecs.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.Ordinal);

    private static string? MapVaapiProfileToCodec(string profile)
    {
        if (profile.Contains("H264", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("AVC", StringComparison.OrdinalIgnoreCase))
        {
            return "h264";
        }

        if (profile.Contains("HEVC", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("H265", StringComparison.OrdinalIgnoreCase))
        {
            return "hevc";
        }

        if (profile.Contains("MPEG2", StringComparison.OrdinalIgnoreCase))
        {
            return "mpeg2video";
        }

        if (profile.Contains("MPEG4", StringComparison.OrdinalIgnoreCase))
        {
            return "mpeg4";
        }

        if (profile.Contains("VP9", StringComparison.OrdinalIgnoreCase))
        {
            return "vp9";
        }

        if (profile.Contains("VP8", StringComparison.OrdinalIgnoreCase))
        {
            return "vp8";
        }

        if (profile.Contains("AV1", StringComparison.OrdinalIgnoreCase))
        {
            return "av1";
        }

        if (profile.Contains("VC1", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("VC-1", StringComparison.OrdinalIgnoreCase))
        {
            return "vc1";
        }

        if (profile.Contains("JPEG", StringComparison.OrdinalIgnoreCase))
        {
            return "mjpeg";
        }

        if (profile.Contains("H263", StringComparison.OrdinalIgnoreCase))
        {
            return "h263";
        }

        if (profile.Contains("MPEG1", StringComparison.OrdinalIgnoreCase))
        {
            return "mpeg1video";
        }

        return null;
    }

    private static string TrimError(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .LastOrDefault(item => item.Length > 0);
        return line is { Length: > 240 } ? line[^240..] : line ?? string.Empty;
    }

    private sealed record VaapiProbe(string Driver, string Label, GpuFormatLimits Format);

    private sealed record NvencProbe(string Driver, GpuFormatLimits Format);

    private sealed record VainfoResult(
        string Driver,
        IReadOnlyList<string> EncodeProfiles,
        IReadOnlySet<string> DecodeCodecs);
}

public sealed record GpuSelectOption(string Value, string Label);

public sealed record GpuAccelOption(
    string Id,
    string Label,
    bool Available,
    IReadOnlyList<GpuSelectOption> Encoders,
    IReadOnlyList<GpuSelectOption> Devices);

public sealed record GpuFormatLimits(
    IReadOnlyList<GpuSelectOption> VideoCodecs,
    IReadOnlyList<GpuSelectOption> H264Profiles,
    IReadOnlyList<GpuSelectOption> Resolutions,
    IReadOnlyList<GpuSelectOption> FrameRates);

public sealed record GpuCapabilities(
    string Summary,
    string Driver,
    IReadOnlyList<GpuSelectOption> VaapiDevices,
    IReadOnlyList<GpuAccelOption> Accelerations,
    IReadOnlyDictionary<string, GpuFormatLimits> Formats,
    IReadOnlyDictionary<string, IReadOnlyList<string>> VaapiDecodeCodecs)
{
    public static GpuCapabilities SoftwareOnly { get; } = new(
        "Software libx264",
        "CPU",
        [],
        [new GpuAccelOption("none", "Software", true, [new("auto", "Auto (match acceleration)"), new("libx264", "libx264")], [])],
        new Dictionary<string, GpuFormatLimits>(StringComparer.OrdinalIgnoreCase)
        {
            ["none"] = new(
                [new("h264", "H.264"), new("mpeg2", "MPEG-2")],
                [new("baseline", "Baseline"), new("main", "Main"), new("high", "High")],
                [
                    new("match", "Match channel (1080p 16:9 or 4:3)"),
                    new("480p", "480p"),
                    new("720p", "720p"),
                    new("1080p", "1080p")
                ],
                [
                    new("23.976", "23.976"),
                    new("24", "24"),
                    new("25", "25"),
                    new("29.97", "29.97"),
                    new("30", "30"),
                    new("50", "50"),
                    new("59.94", "59.94"),
                    new("60", "60")
                ])
        },
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
}
