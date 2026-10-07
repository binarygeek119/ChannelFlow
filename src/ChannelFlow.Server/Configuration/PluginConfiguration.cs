using System.Text.Json.Serialization;
using ChannelFlow.CommercialDetect;
using FinTv.Domain;

namespace FinTv.Configuration;

public class PluginConfiguration
{
    public string ScheduleTimeZone { get; set; } = "America/New_York";

    public bool DebugLogging { get; set; }

    public string? CommercialLibraryId { get; set; }

    public string? CommercialLibraryTag { get; set; } = "channelflow-commercial";

    public int PlayoutDaysToBuild { get; set; } = 14;

    /// <summary>
    /// Seconds to keep a channel encoder running after the last viewer disconnects.
    /// 0 stops immediately. Maximum is 3600 (one hour).
    /// </summary>
    public int StreamIdleTimeoutSeconds { get; set; } = 30;

    public const int MaxStreamIdleTimeoutSeconds = 3600;

    public static int ClampStreamIdleTimeoutSeconds(int value) =>
        Math.Clamp(value, 0, MaxStreamIdleTimeoutSeconds);

    public int HistoryDaysToConsider { get; set; } = 7;

    [JsonPropertyName("publicBaseUrl")]
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Same-network base URL used for Local URL copy and quick pairing when the admin is on
    /// the LAN (for example <c>http://192.168.1.2:8097</c>). Empty falls back to the origin
    /// the browser connected with.
    /// </summary>
    [JsonPropertyName("localBaseUrl")]
    public string? LocalBaseUrl { get; set; }

    /// <summary>
    /// Shared secret for IPTV <c>?apiKey=</c> on M3U and XMLTV URLs.
    /// Generated at startup when missing and edited on the General page.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Jellyfin base URL used for Live TV guide refresh callbacks (legacy plugin installs; optional).
    /// </summary>
    public string? JellyfinPluginUrl { get; set; }

    public EbsBackgroundMusicSource EbsBackgroundMusicSource { get; set; } = EbsBackgroundMusicSource.LocalPacks;

    public string EbsBackgroundMusicLibraryName { get; set; } = "Background Music";

    public string? EbsBackgroundMusicLibraryId { get; set; }

    public EbsSlateVariant EbsSlateVariant { get; set; } = EbsSlateVariant.Usa;

    public EbsDisplayMode EbsDisplayMode { get; set; } = EbsDisplayMode.SlateImage;

    public EbsAudioMode EbsAudioMode { get; set; } = EbsAudioMode.BackgroundMusic;

    public bool AutoRegisterLiveTv { get; set; }

    public string Binarygeek119LogoSetUrl { get; set; } = "bundled";

    public string WeatherStarBaseUrl { get; set; } = "http://127.0.0.1:8080";

    public string WeatherStarPermalinkQuery { get; set; } =
        "hazards=true&current-weather=true&latest-observations=true&hourly=true&hourly-graph=true&travel=true&regional-forecast=true&local-forecast=true&extended-forecast=true&almanac=true&spc-outlook=true&radar=true&stickyKiosk=true&customTextEnable=false&speed=1.00&viewMode=standard&units=us&customText=&mediaVolume=0.75&wide=false&portrait=false&enhanced=false";

    public string? WeatherDefaultLocationQuery { get; set; }

    /// <summary>
    /// <c>auto</c>, <c>us</c> (NOAA), or <c>world</c> (Open-Meteo).
    /// </summary>
    public string WeatherSource { get; set; } = "auto";

    public bool AutoStartPlaywrightDockerSidecar { get; set; }

    public bool AutoStartWeatherStarDocker { get; set; } = true;

    public bool WeatherStarAutoWideForSixteenNine { get; set; } = true;

    public string? WeatherMusicLibraryId { get; set; }

    public string WeatherMusicLibraryName { get; set; } = "";

    /// <summary>
    /// How weather alerts appear on non-weather channels: <c>off</c>, <c>cutin</c>, or <c>ticker</c>.
    /// </summary>
    public string WeatherAlertOverlayMode { get; set; } = "off";

    /// <summary>
    /// Minutes between WeatherStar alert-screen cut-ins on other channels.
    /// </summary>
    public int WeatherAlertCutInIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Seconds to show the WeatherStar alerts screen during a cut-in.
    /// </summary>
    public int WeatherAlertCutInDurationSeconds { get; set; } = 20;

    public CommercialBreakScanSettings CommercialBreakScan { get; set; } = new();

    public CommercialBrainzSettings CommercialBrainz { get; set; } = new();

    public YouTubeSettings YouTube { get; set; } = new();

    public List<CommercialSearchPlaylist> CommercialSearchPlaylists { get; set; } = new();

    public JellyfinLibrarySettings JellyfinLibraries { get; set; } = new();

    public CatalogCleanupSettings CatalogCleanup { get; set; } = new();

    public AiSettings Ai { get; set; } = new();

    public List<Guid> AiPendingAutoApplyChannelIds { get; set; } = new();

    public AiGenerateAllJobState AiGenerateAllJob { get; set; } = new();

    public Ws4kpDockerSettings Ws4kp { get; set; } = new();

    public Ws3kpDockerSettings Ws3kp { get; set; } = new();

    /// <summary>
    /// Live WeatherStar look for MPEG-TS channels that are not a dedicated 3000 preset.
    /// WeatherStar3000 channels always use 3000 fonts/screens.
    /// </summary>
    public string WeatherStarVariant { get; set; } = "ws4kp";

    /// <summary>
    /// Live MPEG-TS ffmpeg encode settings. Empty values follow <c>FFMPEG_*</c> environment defaults.
    /// </summary>
    public TranscodeSettings Transcode { get; set; } = new();

    /// <summary>
    /// Target video/audio format for every live MPEG-TS stream.
    /// </summary>
    public NormalizationSettings Normalization { get; set; } = new();
}

public class TranscodeSettings
{
    /// <summary>
    /// <c>none</c>, <c>vaapi</c>, <c>qsv</c>, or <c>nvenc</c>. Empty means follow <c>FFMPEG_HWACCEL</c>.
    /// </summary>
    public string? HardwareAcceleration { get; set; }

    /// <summary>
    /// Encoder name such as <c>libx264</c>, <c>h264_vaapi</c>, <c>h264_qsv</c>, or <c>h264_nvenc</c>. Empty means auto.
    /// </summary>
    public string? VideoEncoder { get; set; }

    /// <summary>
    /// VAAPI/QSV render node, typically <c>/dev/dri/renderD128</c>. Empty means follow <c>FFMPEG_VAAPI_DEVICE</c>.
    /// </summary>
    public string? VaapiDevice { get; set; }

    /// <summary>
    /// Seconds ffmpeg may encode ahead of wall clock so tuners have a buffer.
    /// 0 paces at real time. Maximum is 600 (10 minutes).
    /// </summary>
    public int RunAheadSeconds { get; set; } = DefaultRunAheadSeconds;

    public const int DefaultRunAheadSeconds = 180;

    public const int MaxRunAheadSeconds = 600;

    public static int ClampRunAheadSeconds(int value) => Math.Clamp(value, 0, MaxRunAheadSeconds);
}

public class NormalizationSettings
{
    public string Resolution { get; set; } = DefaultResolution;

    public string FrameRate { get; set; } = DefaultFrameRate;

    public string VideoCodec { get; set; } = DefaultVideoCodec;

    public string VideoProfile { get; set; } = DefaultVideoProfile;

    public string VideoBitrate { get; set; } = DefaultVideoBitrate;

    public string AudioCodec { get; set; } = DefaultAudioCodec;

    public string AudioChannels { get; set; } = DefaultAudioChannels;

    public string AudioSampleRate { get; set; } = DefaultAudioSampleRate;

    public string AudioBitrate { get; set; } = DefaultAudioBitrate;

    public const string DefaultResolution = "match";
    public const string DefaultFrameRate = "30";
    public const string DefaultVideoCodec = "h264";
    public const string DefaultVideoProfile = "main";
    public const string DefaultVideoBitrate = "auto";
    public const string DefaultAudioCodec = "aac";
    public const string DefaultAudioChannels = "2.0";
    public const string DefaultAudioSampleRate = "48000";
    public const string DefaultAudioBitrate = "192k";

    public static NormalizationSettings CreateDefault() => new();
}

public class Ws4kpDockerSettings : IWeatherStarDockerSettings
{
    public int HostPort { get; set; } = 8080;

    public string Image { get; set; } = "ghcr.io/netbymatt/ws4kp";
}

public class Ws3kpDockerSettings : IWeatherStarDockerSettings
{
    public int HostPort { get; set; } = 8083;

    public string Image { get; set; } = "ghcr.io/netbymatt/ws3kp";
}

public interface IWeatherStarDockerSettings
{
    int HostPort { get; set; }

    string Image { get; set; }
}

public class AiSettings
{
    public bool Enabled { get; set; }

    public AiProvider DefaultProvider { get; set; } = AiProvider.OpenAi;

    public string? OpenAiApiKey { get; set; }

    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    public string? VeniceApiKey { get; set; }

    public string VeniceModel { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// TTS model id. Leave blank to use <c>tts-1</c> for OpenAI or <c>tts-kokoro</c> for Venice.
    /// </summary>
    public string TtsModel { get; set; } = "";

    /// <summary>
    /// Voice id for AI text-to-speech (OpenAI <c>nova</c>, Venice <c>af_sky</c>, etc.).
    /// </summary>
    public string TtsVoice { get; set; } = "nova";

    public int MaxCatalogItemsInPrompt { get; set; } = 250;

    public bool AutoApplyOnChannelAdd { get; set; }

    public bool AutoApplyToAllChannelsOnSave { get; set; }

    /// <summary>
    /// When true, primetime playout steals catalog items whose original air month and day
    /// match that schedule date (any year). Leftover primetime stays the sticky lineup.
    /// Sequential episode order is not advanced for stolen airings.
    /// </summary>
    public bool SimulateOriginalBroadcasting { get; set; }
}

public class CatalogCleanupSettings
{
    /// <summary>
    /// Days a catalog row stays marked missing before the cleanup task deletes it.
    /// </summary>
    public int GracePeriodDays { get; set; } = 7;

    public DateTime? LastCatalogSyncStartedAt { get; set; }

    public DateTime? LastCatalogSyncCompletedAt { get; set; }

    public CatalogCleanupTaskState TaskState { get; set; } = new();

    public CatalogLocalScanTaskState LocalScan { get; set; } = new();
}

public class CatalogCleanupTaskState
{
    public bool IsRunning { get; set; }

    public int MarkedMissing { get; set; }

    public int Removed { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastStartedAt { get; set; }

    public DateTime? LastCompletedAt { get; set; }
}

public class CatalogLocalScanTaskState
{
    public bool IsRunning { get; set; }

    public int TotalItems { get; set; }

    public int ProcessedItems { get; set; }

    public int Found { get; set; }

    public int MarkedMissing { get; set; }

    public int Restored { get; set; }

    public int Skipped { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastStartedAt { get; set; }

    public DateTime? LastCompletedAt { get; set; }
}

public class BlackframeTaskState
{
    public bool IsRunning { get; set; }

    public int TotalItems { get; set; }

    public int ProcessedItems { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastStartedAt { get; set; }

    public DateTime? LastCompletedAt { get; set; }
}

public class AiGenerateAllJobState
{
    public bool IsRunning { get; set; }

    public int TotalDays { get; set; }

    public int TotalChannels { get; set; }

    public int TotalSteps { get; set; }

    public int CompletedSteps { get; set; }

    public int CurrentDay { get; set; }

    public string? CurrentPhase { get; set; }

    public string? CurrentChannelName { get; set; }

    public int LineupsGenerated { get; set; }

    public int LineupsFailed { get; set; }

    public int PlayoutDaysBuilt { get; set; }

    public int PlayoutDaysFailed { get; set; }

    public string? LastError { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public bool WasCancelled { get; set; }

    public DateTime? LastProgressAt { get; set; }

    public bool WasStale { get; set; }
}

/// <summary>
/// Which Jellyfin libraries ChannelFlow should use for TV, movies, music, and music videos.
/// Empty lists mean every matching library.
/// </summary>
public class JellyfinLibrarySettings
{
    public List<Guid> TvLibraryIds { get; set; } = new();

    public List<Guid> MovieLibraryIds { get; set; } = new();

    public List<Guid> MusicLibraryIds { get; set; } = new();

    public List<Guid> MusicVideoLibraryIds { get; set; } = new();

    public List<Guid> HomeVideoLibraryIds { get; set; } = new();

    /// <summary>
    /// Libraries selected for catalog import (kept in sync from Library → Connections).
    /// </summary>
    public List<JellyfinLibraryInfo> Libraries { get; set; } = new();

    public bool Allows(BaseItemKind kind, Guid? libraryId)
        => Allows(kind, libraryId, libraryName: null);

    public bool Allows(BaseItemKind kind, Guid? libraryId, string? libraryName)
    {
        if (kind is BaseItemKind.Folder or BaseItemKind.Playlist)
        {
            return true;
        }

        var selected = kind switch
        {
            BaseItemKind.Series or BaseItemKind.Episode or BaseItemKind.Season => TvLibraryIds,
            BaseItemKind.Movie => MovieLibraryIds,
            BaseItemKind.Audio => MusicLibraryIds,
            BaseItemKind.MusicVideo => MusicVideoLibraryIds,
            BaseItemKind.Video => HomeVideoLibraryIds,
            _ => null
        };

        if (selected is not { Count: > 0 })
        {
            return true;
        }

        if (libraryId is Guid id && id != Guid.Empty && selected.Contains(id))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(libraryName)
            && Libraries.Any(library =>
                selected.Contains(library.Id)
                && library.Name.Equals(libraryName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Catalog rows sometimes lack LibraryId after a sync. Don't drop them or year
        // channels like FlashBack TV end up with an empty AI catalog.
        return libraryId is null || libraryId == Guid.Empty;
    }

    public static List<Guid> Normalize(IEnumerable<Guid>? ids)
        => ids?
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList()
            ?? new List<Guid>();
}

/// <summary>
/// A Jellyfin media library reported by the plugin.
/// </summary>
public class JellyfinLibraryInfo
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? CollectionType { get; set; }
}

public class WeatherGuideSlotCache
{
    public string Title { get; set; } = string.Empty;

    public string? SubTitle { get; set; }

    public string? Description { get; set; }

    public List<string> Categories { get; set; } = new();

    public string? ForecastDate { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
}
