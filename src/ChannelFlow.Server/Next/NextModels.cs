using System.Text.Json.Serialization;

namespace FinTv.Next;

/// <summary>
/// Version URIs understood by the ErsatzTV next streaming engine.
/// </summary>
public static class NextSchemas
{
    public const string Lineup = "https://ersatztv.org/lineup/version/0.0.1";

    public const string Channel = "https://ersatztv.org/channel/version/0.1.1";

    public const string Playout = "https://ersatztv.org/playout/version/0.0.5";
}

/// <summary>
/// Top-level <c>lineup.json</c> document consumed by ErsatzTV next.
/// </summary>
public class NextLineupConfig
{
    public string? Version { get; set; } = NextSchemas.Lineup;

    public NextServerConfig Server { get; set; } = new();

    public NextOutputConfig Output { get; set; } = new();

    public NextXmltvConfig? Xmltv { get; set; }

    public List<NextChannelEntry> Channels { get; set; } = [];
}

public class NextServerConfig
{
    public string BindAddress { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 8409;
}

public class NextOutputConfig
{
    public string Folder { get; set; } = "/tmp/next-hls";
}

public class NextXmltvConfig
{
    public string Folder { get; set; } = "";
}

public class NextChannelEntry
{
    public string Number { get; set; } = "";

    public string Name { get; set; } = "";

    public string Config { get; set; } = "";

    [JsonPropertyName("tvg_id")]
    public string? TvgId { get; set; }

    public string? Logo { get; set; }

    public string? Group { get; set; }
}

/// <summary>
/// Per-channel <c>channel.json</c> document: where the playout lives and how the
/// engine normalizes the video/audio it streams.
/// </summary>
public class NextChannelConfig
{
    public string? Version { get; set; } = NextSchemas.Channel;

    public NextPlayoutConfig Playout { get; set; } = new();

    public NextFfmpegConfig Ffmpeg { get; set; } = new();

    public NextNormalizationConfig Normalization { get; set; } = new();

    public NextFallbackConfig? Fallback { get; set; }
}

public class NextPlayoutConfig
{
    public string Folder { get; set; } = "./playout";
}

public class NextFfmpegConfig
{
    [JsonPropertyName("ffmpeg_path")]
    public string? FfmpegPath { get; set; }

    [JsonPropertyName("ffprobe_path")]
    public string? FfprobePath { get; set; }

    [JsonPropertyName("disabled_filters")]
    public List<string> DisabledFilters { get; set; } = [];
}

public class NextFallbackConfig
{
    [JsonPropertyName("show_error")]
    public bool ShowError { get; set; }
}

public class NextNormalizationConfig
{
    public NextAudioNormalization Audio { get; set; } = new();

    public NextVideoNormalization Video { get; set; } = new();

    public NextSubtitleNormalization Subtitle { get; set; } = new();
}

public class NextAudioNormalization
{
    public string Mode { get; set; } = "transcode";

    [JsonPropertyName("copy_formats")]
    public List<string>? CopyFormats { get; set; }

    public string? Format { get; set; }

    [JsonPropertyName("bitrate_kbps")]
    public int? BitrateKbps { get; set; }

    [JsonPropertyName("buffer_kbps")]
    public int? BufferKbps { get; set; }

    public int? Channels { get; set; }

    [JsonPropertyName("sample_rate_hz")]
    public int? SampleRateHz { get; set; }

    [JsonPropertyName("normalize_loudness")]
    public bool NormalizeLoudness { get; set; }
}

public class NextVideoNormalization
{
    public string Mode { get; set; } = "transcode";

    [JsonPropertyName("copy_formats")]
    public List<string>? CopyFormats { get; set; }

    public string? Format { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    [JsonPropertyName("bitrate_kbps")]
    public int? BitrateKbps { get; set; }

    [JsonPropertyName("buffer_kbps")]
    public int? BufferKbps { get; set; }

    public string? Profile { get; set; }

    public string? Accel { get; set; }

    [JsonPropertyName("vaapi_device")]
    public string? VaapiDevice { get; set; }

    [JsonPropertyName("vaapi_driver")]
    public string? VaapiDriver { get; set; }

    [JsonPropertyName("frame_rate")]
    public string? FrameRate { get; set; }

    [JsonPropertyName("scaling_mode")]
    public string ScalingMode { get; set; } = "scale_and_pad";
}

public class NextSubtitleNormalization
{
    public string Mode { get; set; } = "burn";
}

/// <summary>
/// A single playout window file ({start}_{finish}.json).
/// </summary>
public class NextPlayout
{
    public string? Version { get; set; } = NextSchemas.Playout;

    public List<NextPlayoutItem> Items { get; set; } = [];
}

public class NextPlayoutItem
{
    public string Id { get; set; } = "";

    public string Start { get; set; } = "";

    public string Finish { get; set; } = "";

    public NextPlayoutItemSource? Source { get; set; }

    public List<NextGraphicsLayer>? Graphics { get; set; }
}

/// <summary>
/// Discriminated union on <c>source_type</c>; a playout item supplies its media
/// via a local file, an HTTP/RTSP stream, a command, or a dynamic resolver.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "source_type")]
[JsonDerivedType(typeof(NextLocalSource), "local")]
[JsonDerivedType(typeof(NextHttpSource), "http")]
[JsonDerivedType(typeof(NextRtspSource), "rtsp")]
[JsonDerivedType(typeof(NextDynamicSource), "dynamic")]
public abstract class NextPlayoutItemSource
{
}

public class NextLocalSource : NextPlayoutItemSource
{
    public string Path { get; set; } = "";

    [JsonPropertyName("in_point_ms")]
    public long? InPointMs { get; set; }

    [JsonPropertyName("out_point_ms")]
    public long? OutPointMs { get; set; }
}

public class NextHttpSource : NextPlayoutItemSource
{
    public string Uri { get; set; } = "";

    [JsonPropertyName("is_live")]
    public bool? IsLive { get; set; }
}

/// <summary>
/// A live RTSP feed next pulls and transcodes itself (weather/news channels pointed
/// at an RTSP camera instead of ChannelFlow's own compositors).
/// </summary>
public class NextRtspSource : NextPlayoutItemSource
{
    public string Uri { get; set; } = "";

    /// <summary>Optional microsecond connection/read timeout; null lets next decide.</summary>
    [JsonPropertyName("timeout_us")]
    public long? TimeoutUs { get; set; }
}

/// <summary>
/// A placeholder item resolved over HTTP at playback time: the "push things to
/// play" hook ChannelFlow uses to drive the engine from its own schedule DB.
/// </summary>
public class NextDynamicSource : NextPlayoutItemSource
{
    public string Uri { get; set; } = "";
}

public class NextGraphicsLayer
{
    public NextPlayoutItemSource Source { get; set; } = new NextLocalSource();

    public string Location { get; set; } = "bottom_right";

    [JsonPropertyName("width_percent")]
    public int? WidthPercent { get; set; }

    [JsonPropertyName("horizontal_margin_percent")]
    public int? HorizontalMarginPercent { get; set; }

    [JsonPropertyName("vertical_margin_percent")]
    public int? VerticalMarginPercent { get; set; }

    [JsonPropertyName("opacity_percent")]
    public int? OpacityPercent { get; set; }

    [JsonPropertyName("within_source_content")]
    public bool? WithinSourceContent { get; set; }
}