using FinTv.Domain;
using FinTv.Services;

namespace FinTv.Api;

/// <summary>
/// Channel create/update payload from the admin UI.
/// </summary>
public class ChannelUpsertRequest
{
    public decimal Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public ChannelContentType ContentType { get; set; }

    public AspectRatioMode AspectRatio { get; set; }

    public bool ScanlinesEnabled { get; set; }

    public BugPlacementMode BugPlacement { get; set; } = BugPlacementMode.BottomRight;

    public Guid? LogoSetId { get; set; }

    public string? LogoFileName { get; set; }

    public string AudioLanguage { get; set; } = "eng";

    public string? WeatherLocationQuery { get; set; }

    public string? RtspUrl { get; set; }

    public string? FilterJson { get; set; }

    public ChannelCatalogMode? CatalogMode { get; set; }

    public string? AiFineTunePrompt { get; set; }

    public Guid? CommercialPresetId { get; set; }

    public List<Guid>? CommercialSearchPlaylistIds { get; set; }

    public Channel ToChannel()
    {
        var channel = new Channel
        {
            Number = Number,
            Name = Name,
            Enabled = Enabled,
            ContentType = ContentType,
            AspectRatio = AspectRatio,
            ScanlinesEnabled = ScanlinesEnabled,
            BugPlacement = BugPlacement,
            LogoSetId = LogoSetId,
            LogoFileName = LogoFileName,
            AudioLanguage = AudioLanguage,
            WeatherLocationQuery = WeatherLocationQuery,
            RtspUrl = string.IsNullOrWhiteSpace(RtspUrl) ? null : RtspUrl.Trim(),
            FilterJson = FilterJson,
            CatalogMode = CatalogMode,
            AiFineTunePrompt = AiFineTunePrompt,
            CommercialPresetId = CommercialPresetId,
            CommercialSearchPlaylistIds = CommercialSearchPlaylistIds ?? new List<Guid>()
        };

        if (channel.ContentType == ChannelContentType.Weather)
        {
            var location = WeatherStarChannelService.ResolveLocationQuery(channel.WeatherLocationQuery);
            channel.WeatherLocationQuery = string.IsNullOrWhiteSpace(location) ? null : location;
        }

        // RTSP replaces the compositor only on the continuous-live (weather/news) channels.
        if (channel.ContentType is not (ChannelContentType.Weather or ChannelContentType.News))
        {
            channel.RtspUrl = null;
        }

        return channel;
    }
}
