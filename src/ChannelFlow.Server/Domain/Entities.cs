namespace FinTv.Domain;

using System.Text.Json.Serialization;

public class Channel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public decimal Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public ChannelContentType ContentType { get; set; } = ChannelContentType.Movie;

    public AspectRatioMode AspectRatio { get; set; } = AspectRatioMode.SixteenNine;

    public bool ScanlinesEnabled { get; set; }

    public Guid? LogoSetId { get; set; }

    public string? ChannelLogoPath { get; set; }

    public string? LogoFileName { get; set; }

    public BugPlacementMode BugPlacement { get; set; } = BugPlacementMode.BottomRight;

    public Guid? CommercialPresetId { get; set; }

    public string AudioLanguage { get; set; } = "eng";

    public int PlayoutSeed { get; set; } = 42;

    public string PlayoutAnchorJson { get; set; } = "{}";

    public DateTime? LastPlayoutBuiltAt { get; set; }

    public string? WeatherLocationQuery { get; set; }

    /// <summary>
    /// Optional continuous RTSP feed (weather cam, security cam, etc.) that replaces the
    /// built-in compositor on weather/news channels. Handed to the ErsatzTV next engine,
    /// or encoded by ChannelFlow's own ffmpeg when next is off.
    /// </summary>
    public string? RtspUrl { get; set; }

    public string? FilterJson { get; set; }

    public ChannelCatalogMode? CatalogMode { get; set; }

    public string? AiFineTunePrompt { get; set; }

    public string? AiPlayoutTemplateId { get; set; }

    [JsonIgnore]
    public string? CommercialSearchPlaylistIdsJson { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<Guid> CommercialSearchPlaylistIds
    {
        get => ParseCommercialSearchPlaylistIds(CommercialSearchPlaylistIdsJson);
        set => CommercialSearchPlaylistIdsJson = SerializeCommercialSearchPlaylistIds(value);
    }

    [JsonIgnore]
    public bool IsContinuousLive => ContentType is ChannelContentType.Weather or ChannelContentType.News;

    [JsonIgnore]
    public LogoSet? LogoSet { get; set; }

    [JsonIgnore]
    public CommercialPreset? CommercialPreset { get; set; }

    [JsonIgnore]
    public Lineup? DefaultLineup { get; set; }

    [JsonIgnore]
    public ICollection<LineupOverride> Overrides { get; set; } = new List<LineupOverride>();

    [JsonIgnore]
    public ICollection<PlayoutItem> PlayoutItems { get; set; } = new List<PlayoutItem>();

    [JsonIgnore]
    public ICollection<PlayoutHistoryEntry> History { get; set; } = new List<PlayoutHistoryEntry>();

    public static List<Guid> ParseCommercialSearchPlaylistIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<Guid>();
        }

        try
        {
            return FinTvJson.Deserialize<List<Guid>>(json)?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList()
                ?? new List<Guid>();
        }
        catch
        {
            return new List<Guid>();
        }
    }

    public static string? SerializeCommercialSearchPlaylistIds(IEnumerable<Guid>? ids)
    {
        var list = ids?
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList()
            ?? new List<Guid>();
        return list.Count == 0 ? null : FinTvJson.Serialize(list);
    }
}

public class LogoSet
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    public string StoragePath { get; set; } = string.Empty;

    public DateTime? LastSyncedAt { get; set; }

    public ICollection<LogoSetEntry> Entries { get; set; } = new List<LogoSetEntry>();
}

public class LogoSetEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LogoSetId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    [JsonIgnore]
    public LogoSet? LogoSet { get; set; }
}

public class Lineup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public string Name { get; set; } = "Default";

    public bool IsDefault { get; set; } = true;

    [JsonIgnore]
    public Channel? Channel { get; set; }

    public ICollection<LineupSlot> Slots { get; set; } = new List<LineupSlot>();
}

public class LineupOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public LineupOverrideKind Kind { get; set; }

    public DayOfWeek? DayOfWeek { get; set; }

    public DateOnly? SpecificDate { get; set; }

    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public Channel? Channel { get; set; }

    public ICollection<LineupSlot> Slots { get; set; } = new List<LineupSlot>();
}

public class LineupSlot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? LineupId { get; set; }

    public Guid? LineupOverrideId { get; set; }

    public int SlotIndex { get; set; }

    public int SpanSlots { get; set; } = 1;

    public bool IsRerunSlot { get; set; }

    [JsonIgnore]
    public Lineup? Lineup { get; set; }

    [JsonIgnore]
    public LineupOverride? LineupOverride { get; set; }

    public ICollection<SlotCandidate> Candidates { get; set; } = new List<SlotCandidate>();
}

public class SlotCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LineupSlotId { get; set; }

    public SlotCandidateKind Kind { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public string? CollectionName { get; set; }

    public string? FilterJson { get; set; }

    public Guid? FinTvListId { get; set; }

    public int Weight { get; set; } = 1;

    public int SortOrder { get; set; }

    [JsonIgnore]
    public LineupSlot? LineupSlot { get; set; }
}

public class PlayoutItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public Guid? CommercialId { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public DateTime Start { get; set; }

    public DateTime Finish { get; set; }

    public TimeSpan InPoint { get; set; }

    public TimeSpan OutPoint { get; set; }

    public FillerKind FillerKind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? GuideGroup { get; set; }

    public string? ExternalUrl { get; set; }

    public bool IsVirtual { get; set; }

    public VirtualContentSource VirtualSource { get; set; }

    [JsonIgnore]
    public Channel? Channel { get; set; }
}

public class PlayoutHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public DateTime AiredAt { get; set; }

    public string Title { get; set; } = string.Empty;

    [JsonIgnore]
    public Channel? Channel { get; set; }
}

public class CommercialPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Default";

    public CommercialBreakMode BreakMode { get; set; } = CommercialBreakMode.ChaptersThenTimer;

    public int TimerIntervalMinutes { get; set; } = 12;

    public int PreRollCount { get; set; } = 2;

    public int PostRollCount { get; set; } = 2;

    public int MidRollCount { get; set; } = 2;

    [JsonIgnore]
    public ICollection<Channel> Channels { get; set; } = new List<Channel>();
}

public class Commercial
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public CommercialSource Source { get; set; } = CommercialSource.Jellyfin;

    public Guid JellyfinItemId { get; set; }

    public string? YouTubeUrl { get; set; }

    public string? YouTubeVideoId { get; set; }

    public string? CommercialBrainzVideoSbid { get; set; }

    public string Title { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }

    public string? Brand { get; set; }

    public int? Year { get; set; }

    public int? Decade { get; set; }

    public string? Network { get; set; }

    public string? ChannelName { get; set; }

    public int? AgeLimit { get; set; }

    public string? TagsJson { get; set; }

    public bool IsBanned { get; set; }

    public bool IsAdultRated { get; set; }

    public bool IsLateNight { get; set; }

    public bool IsSpoof { get; set; }

    public bool IsFake { get; set; }

    public bool IsReal { get; set; }

    public bool IsAiEnhanced { get; set; }

    public DateTime? LastScannedAt { get; set; }

    public ICollection<CommercialChapter> Chapters { get; set; } = new List<CommercialChapter>();
}

public class CommercialChapter
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CommercialId { get; set; }

    public TimeSpan Start { get; set; }

    public TimeSpan End { get; set; }

    public string? Name { get; set; }

    public bool DetectedByBlackframe { get; set; }

    [JsonIgnore]
    public Commercial? Commercial { get; set; }
}

public class FinTvList
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public Guid JellyfinPlaylistId { get; set; }

    public ListPlaybackMode PlaybackMode { get; set; } = ListPlaybackMode.Sequential;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SpecialPresentation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public DayOfWeek DayOfWeek { get; set; }

    public int SlotIndex { get; set; }

    public int SpanSlots { get; set; } = 1;

    [JsonIgnore]
    public Channel? Channel { get; set; }

    public ICollection<SpecialPresentationCandidate> Candidates { get; set; } = new List<SpecialPresentationCandidate>();
}

public class SpecialPresentationCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SpecialPresentationId { get; set; }

    public SlotCandidateKind Kind { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public string? CollectionName { get; set; }

    public string? FilterJson { get; set; }

    public Guid? FinTvListId { get; set; }

    public int Weight { get; set; } = 1;

    public int SortOrder { get; set; }

    [JsonIgnore]
    public SpecialPresentation? SpecialPresentation { get; set; }
}
