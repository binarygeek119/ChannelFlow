using FinTv.Configuration;
using FinTv.Domain;

namespace FinTv.Streaming;

/// <summary>
/// User-selected MPEG-TS target format. Saved settings apply on the next encode without a restart.
/// </summary>
public sealed class StreamNormalizationService
{
    private readonly object _gate = new();
    private NormalizationTarget _current = NormalizationTarget.Default;

    public NormalizationTarget Current
    {
        get { lock (_gate) return _current; }
    }

    public void ApplyFromSaved(NormalizationSettings? saved)
    {
        var target = NormalizationTarget.FromSettings(saved);
        lock (_gate)
        {
            _current = target;
        }
    }

    public (int Width, int Height) ResolveSize(AspectRatioMode aspect)
        => Current.ResolveSize(aspect);

    public object Describe()
    {
        var target = Current;
        return new
        {
            resolution = target.Resolution,
            frameRate = target.FrameRate,
            videoCodec = target.VideoCodec,
            videoProfile = target.VideoProfile,
            videoBitrate = target.VideoBitrate,
            audioCodec = target.AudioCodec,
            audioChannels = target.AudioChannels,
            audioSampleRate = target.AudioSampleRate.ToString(),
            audioBitrate = target.AudioBitrate,
            normalizationMode = target.NormalizationMode,
            summary = target.Summary
        };
    }
}

public readonly record struct NormalizationTarget(
    string Resolution,
    string FrameRate,
    string FpsFilter,
    string FpsOutput,
    int Gop,
    string VideoCodec,
    string VideoProfile,
    string VideoBitrate,
    string AudioCodec,
    string AudioChannels,
    string AudioLayout,
    int AudioChannelCount,
    int AudioSampleRate,
    string AudioBitrate,
    string NormalizationMode)
{
    public static NormalizationTarget Default => FromSettings(null);

    public NormalizationSettings ToSettings()
        => new()
        {
            Resolution = Resolution,
            FrameRate = FrameRate,
            VideoCodec = VideoCodec,
            VideoProfile = VideoProfile,
            VideoBitrate = VideoBitrate,
            AudioCodec = AudioCodec,
            AudioChannels = AudioChannels,
            AudioSampleRate = AudioSampleRate.ToString(),
            AudioBitrate = AudioBitrate,
            NormalizationMode = NormalizationMode
        };

    public string Summary
        => $"{ModePrefix}{VideoLabel} {ProfileLabel} {BitrateLabel} @ {SizeLabel} {FrameRate} fps, {AudioLabel}";

    public bool IsMpeg2 => VideoCodec == "mpeg2";

    /// <summary>
    /// Standard AC-3 is 5.1 max. 7.1/7.2 use E-AC-3 so the extra speakers are kept.
    /// </summary>
    public string EncoderAudioCodec
        => AudioCodec == "ac3" && AudioChannelCount > 6 ? "eac3" : AudioCodec;

    public string Level => FrameRate is "50" or "59.94" or "60" ? "4.2" : "4.1";

    public (int Width, int Height) ResolveSize(AspectRatioMode aspect)
    {
        var fourThree = aspect == AspectRatioMode.FourThree;
        return Resolution switch
        {
            "480p" => fourThree ? (640, 480) : (854, 480),
            "720p" => fourThree ? (960, 720) : (1280, 720),
            "1080p" => fourThree ? (1440, 1080) : (1920, 1080),
            _ => fourThree ? (1440, 1080) : (1920, 1080)
        };
    }

    public static NormalizationTarget FromSettings(NormalizationSettings? saved)
    {
        var resolution = Pick(saved?.Resolution, NormalizationSettings.DefaultResolution, "match", "480p", "720p", "1080p");
        var frameRate = Pick(saved?.FrameRate, NormalizationSettings.DefaultFrameRate,
            "23.976", "24", "25", "29.97", "30", "50", "59.94", "60");
        var videoCodec = Pick(saved?.VideoCodec, NormalizationSettings.DefaultVideoCodec, "h264", "mpeg2");
        var videoProfile = Pick(saved?.VideoProfile, NormalizationSettings.DefaultVideoProfile, "baseline", "main", "high");
        var videoBitrate = Pick(saved?.VideoBitrate, NormalizationSettings.DefaultVideoBitrate,
            "auto", "2000k", "4000k", "6000k", "8000k");
        var audioCodec = Pick(saved?.AudioCodec, NormalizationSettings.DefaultAudioCodec, "aac", "ac3");
        var audioChannels = NormalizeChannelLayout(saved?.AudioChannels);
        var sampleRate = Pick(saved?.AudioSampleRate, NormalizationSettings.DefaultAudioSampleRate, "44100", "48000");
        var audioBitrate = Pick(saved?.AudioBitrate, NormalizationSettings.DefaultAudioBitrate,
            "128k", "192k", "256k", "320k", "448k", "640k");
        var normalizationMode = Pick(saved?.NormalizationMode, NormalizationSettings.DefaultNormalizationMode,
            "transcode", "copy");
        var (fpsFilter, fpsOutput, gop) = FrameRateParts(frameRate);
        var (layout, count) = ChannelLayoutParts(audioChannels);
        return new NormalizationTarget(
            resolution,
            frameRate,
            fpsFilter,
            fpsOutput,
            gop,
            videoCodec,
            videoProfile,
            videoBitrate,
            audioCodec,
            audioChannels,
            layout,
            count,
            sampleRate == "44100" ? 44100 : 48000,
            audioBitrate,
            normalizationMode);
    }

    private string ModePrefix => NormalizationMode == "copy" ? "Stream copy / " : "";

    private string VideoLabel => VideoCodec == "mpeg2" ? "MPEG-2" : "H.264";

    private string ProfileLabel => VideoCodec == "mpeg2" ? "" : VideoProfile;

    private string BitrateLabel => VideoBitrate == "auto" ? "" : VideoBitrate;

    private string SizeLabel => Resolution switch
    {
        "480p" => "480p",
        "720p" => "720p",
        "1080p" => "1080p",
        _ => "channel 1080p"
    };

    private string AudioLabel
    {
        get
        {
            var codec = EncoderAudioCodec == "eac3" ? "E-AC-3" : AudioCodec.ToUpperInvariant();
            return $"{codec} {AudioChannels} {AudioSampleRate / 1000} kHz {AudioBitrate}";
        }
    }

    private static string NormalizeChannelLayout(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "2" or "2.0" or "stereo" or "mono" => "2.0",
            "5.1" or "surround" => "5.1",
            "7.1" => "7.1",
            "7.2" => "7.2",
            _ => NormalizationSettings.DefaultAudioChannels
        };
    }

    private static (string Layout, int Count) ChannelLayoutParts(string channels)
        => channels switch
        {
            "5.1" => ("5.1", 6),
            "7.1" => ("7.1", 8),
            "7.2" => ("7.2", 9),
            _ => ("stereo", 2)
        };

    private static (string Filter, string Output, int Gop) FrameRateParts(string frameRate)
        => frameRate switch
        {
            "23.976" => ("24000/1001", "24000/1001", 24),
            "24" => ("24", "24", 24),
            "25" => ("25", "25", 25),
            "29.97" => ("30000/1001", "30000/1001", 30),
            "50" => ("50", "50", 50),
            "59.94" => ("60000/1001", "60000/1001", 60),
            "60" => ("60", "60", 60),
            _ => ("30", "30", 30)
        };

    private static string Pick(string? value, string fallback, params string[] allowed)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return allowed.Any(option => option.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            ? allowed.First(option => option.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            : fallback;
    }
}
