using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FinTv;
using FinTv.Auth;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

public class EpgService
{
    private const string MusicVideoHourGuideGroup = "music-video-hour";
    private static readonly string[] MusicVideoHourCategories = ["Music Video"];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly FinTvDbContext _db;
    private readonly HolidayChannelService _holidays;
    private readonly GuideMetadataService _guideMetadata;
    private readonly WeatherGuideMetadataService _weatherGuideMetadata;

    public EpgService(
        FinTvDbContext db,
        HolidayChannelService holidays,
        GuideMetadataService guideMetadata,
        WeatherGuideMetadataService weatherGuideMetadata)
    {
        _db = db;
        _holidays = holidays;
        _guideMetadata = guideMetadata;
        _weatherGuideMetadata = weatherGuideMetadata;
    }

    public async Task<byte[]> GenerateXmlTvBytesAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var doc = await BuildXmlTvDocumentAsync(baseUrl, cancellationToken);
        return SerializeUtf8(doc);
    }

    public async Task<string> GenerateXmlTvAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        return Utf8NoBom.GetString(await GenerateXmlTvBytesAsync(baseUrl, cancellationToken));
    }

    /// <summary>
    /// Builds a JSON TV guide for the admin Web UI from current playout.
    /// </summary>
    public async Task<TvGuidePage> GetGuideAsync(
        DateTime fromUtc,
        DateTime toUtc,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        if (toUtc <= fromUtc)
        {
            toUtc = fromUtc.AddHours(6);
        }

        var nowUtc = DateTime.UtcNow;
        var tz = ScheduleTimeZoneHelper.ResolveScheduleTimeZone();
        var channels = await _db.Channels.Where(c => c.Enabled).OrderBy(c => c.Number).AsNoTracking()
            .ToListAsync(cancellationToken);
        var items = await _db.PlayoutItems
            .Where(p =>
                p.Finish > fromUtc
                && p.Start < toUtc
                && (p.GuideGroup == null || (p.GuideGroup != "commercial" && p.GuideGroup != LogoBumperService.GuideGroup)))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var channelsById = channels.ToDictionary(c => c.Id);
        var prepared = await PrepareGuideItemsAsync(items, channelsById, tz, cancellationToken);
        items = prepared.Items;
        var shortBlocks = prepared.ShortBlocks;

        var metadataByItemId = _guideMetadata.ResolveBatch(items.Select(i => i.JellyfinItemId));
        var weatherItems = items
            .Where(i => i.IsVirtual && i.VirtualSource == VirtualContentSource.WeatherStar)
            .ToList();
        var weatherMetadataByPlayoutId = await _weatherGuideMetadata.ResolveAsync(
            weatherItems,
            channelsById,
            channel => GetLogoUrl(channel, baseUrl),
            cancellationToken);

        var programs = new List<TvGuideProgram>(items.Count);
        foreach (var item in items)
        {
            var start = AsUtc(item.Start);
            var finish = AsUtc(item.Finish);
            if (IsMusicVideoHourBlock(item) && channelsById.TryGetValue(item.ChannelId, out var musicChannel))
            {
                programs.Add(new TvGuideProgram
                {
                    Id = item.Id,
                    ChannelId = item.ChannelId,
                    Start = start,
                    Finish = finish,
                    Title = musicChannel.Name,
                    Categories = MusicVideoHourCategories,
                    PosterUrl = GetLogoUrl(musicChannel, baseUrl),
                    IsNow = start <= nowUtc && finish > nowUtc
                });
                continue;
            }

            GuideProgramMetadata? metadata = null;
            if (weatherMetadataByPlayoutId.TryGetValue(item.Id, out var weatherMetadata))
            {
                metadata = weatherMetadata;
            }
            else if (item.JellyfinItemId.HasValue)
            {
                metadataByItemId.TryGetValue(item.JellyfinItemId.Value, out metadata);
            }

            var posterUrl = !string.IsNullOrWhiteSpace(metadata?.IconUrl)
                ? metadata.IconUrl
                : GuideMetadataService.GetUiPosterUrl(metadata?.PosterItemId ?? item.JellyfinItemId);

            var title = string.IsNullOrWhiteSpace(metadata?.Title) ? item.Title : metadata.Title;
            var subTitle = metadata?.SubTitle;
            var description = metadata?.Description;
            var episode = metadata?.EpisodeOnScreen;
            IReadOnlyList<TvGuideBlockEpisode>? blockEpisodes = null;
            if (shortBlocks.TryGetValue(item.Id, out var shortBlock) && shortBlock.Parts.Count > 0)
            {
                title = shortBlock.SeriesName;
                subTitle = null;
                description = CombineShortEpisodeDescription(shortBlock);
                episode = shortBlock.Parts.Count == 1 ? shortBlock.Parts[0].Episode : null;
                blockEpisodes = shortBlock.Parts;
            }

            programs.Add(new TvGuideProgram
            {
                Id = item.Id,
                ChannelId = item.ChannelId,
                Start = start,
                Finish = finish,
                Title = title,
                SubTitle = subTitle,
                Description = description,
                Episode = episode,
                Categories = metadata?.Categories ?? Array.Empty<string>(),
                Year = metadata?.ProductionYear,
                Rating = metadata?.OfficialRating,
                PosterUrl = posterUrl,
                IsNow = start <= nowUtc && finish > nowUtc,
                IsVirtual = item.IsVirtual,
                Episodes = blockEpisodes
            });
        }

        return new TvGuidePage
        {
            From = fromUtc,
            To = toUtc,
            Now = nowUtc,
            TimeZone = tz.Id,
            Channels = channels.Select(channel => new TvGuideChannel
            {
                Id = channel.Id,
                Number = ChannelNumbers.Format(channel.Number),
                Name = channel.Name,
                LogoUrl = GetLogoUrl(channel, baseUrl),
                ContentType = channel.ContentType.ToString()
            }).ToList(),
            Programs = programs
        };
    }

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// Combines chapter-split segments of the same show so the guide shows one programme.
    /// Mid-break commercials are already excluded from <paramref name="items"/>.
    /// </summary>
    private static List<PlayoutItem> MergeSplitPrograms(List<PlayoutItem> items)
    {
        var merged = new List<PlayoutItem>();
        foreach (var item in items.OrderBy(i => i.ChannelId).ThenBy(i => i.Start))
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (last.ChannelId == item.ChannelId
                    && last.JellyfinItemId.HasValue
                    && last.JellyfinItemId == item.JellyfinItemId
                    && item.Start <= last.Finish.AddMinutes(8))
                {
                    if (item.Finish > last.Finish)
                    {
                        last.Finish = item.Finish;
                    }

                    continue;
                }
            }

            merged.Add(item);
        }

        return merged;
    }

    private async Task<PreparedGuideItems> PrepareGuideItemsAsync(
        List<PlayoutItem> items,
        IReadOnlyDictionary<Guid, Channel> channelsById,
        TimeZoneInfo tz,
        CancellationToken cancellationToken)
    {
        items = MergeSplitPrograms(items);
        var episodes = await LoadEpisodeGuideRowsAsync(items.Select(i => i.JellyfinItemId), cancellationToken);
        var shortBlocks = new Dictionary<Guid, ShortEpisodeBlock>();
        items = CollapseShortEpisodeBlocks(items, episodes, shortBlocks);
        items = CollapseMusicVideoHours(items, channelsById, tz);
        PadProgramsToHalfHour(items, tz);
        return new PreparedGuideItems(items, shortBlocks);
    }

    private async Task<Dictionary<Guid, EpisodeGuideRow>> LoadEpisodeGuideRowsAsync(
        IEnumerable<Guid?> ids,
        CancellationToken cancellationToken)
    {
        var list = ids.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (list.Count == 0)
        {
            return new Dictionary<Guid, EpisodeGuideRow>();
        }

        var rows = await _db.Episodes.AsNoTracking()
            .Where(e => list.Contains(e.Id))
            .Select(e => new
            {
                e.Id,
                e.SeriesId,
                e.SeriesName,
                e.Name,
                e.Plot,
                e.SeasonNumber,
                e.EpisodeNumber,
                e.RuntimeTicks
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.Id,
            row => new EpisodeGuideRow
            {
                Id = row.Id,
                SeriesId = row.SeriesId,
                SeriesName = row.SeriesName,
                Name = row.Name,
                Plot = row.Plot,
                SeasonNumber = row.SeasonNumber,
                EpisodeNumber = row.EpisodeNumber,
                Runtime = row.RuntimeTicks is long ticks && ticks > 0 ? TimeSpan.FromTicks(ticks) : null
            });
    }

    /// <summary>
    /// Consecutive shorts of the same series (Looney Tunes, Rugrats) become one named block.
    /// </summary>
    private static List<PlayoutItem> CollapseShortEpisodeBlocks(
        List<PlayoutItem> items,
        IReadOnlyDictionary<Guid, EpisodeGuideRow> episodes,
        Dictionary<Guid, ShortEpisodeBlock> blocks)
    {
        var merged = new List<PlayoutItem>();
        foreach (var item in items.OrderBy(i => i.ChannelId).ThenBy(i => i.Start))
        {
            if (merged.Count > 0
                && TryGetShortEpisode(merged[^1], episodes, out var lastEpisode)
                && TryGetShortEpisode(item, episodes, out var thisEpisode)
                && merged[^1].ChannelId == item.ChannelId
                && lastEpisode.SeriesId == thisEpisode.SeriesId
                && item.Start <= merged[^1].Finish.Add(ShortEpisodeBlocks.MaxGuideMergeGap))
            {
                var last = merged[^1];
                if (item.Finish > last.Finish)
                {
                    last.Finish = item.Finish;
                }

                if (blocks.TryGetValue(last.Id, out var block))
                {
                    block.Parts.Add(ToBlockEpisode(thisEpisode));
                }

                continue;
            }

            merged.Add(item);
            if (TryGetShortEpisode(item, episodes, out var episode))
            {
                blocks[item.Id] = new ShortEpisodeBlock
                {
                    SeriesName = string.IsNullOrWhiteSpace(episode.SeriesName) ? episode.Name : episode.SeriesName,
                    Parts = [ToBlockEpisode(episode)]
                };
            }
        }

        return merged;
    }

    private static bool TryGetShortEpisode(
        PlayoutItem item,
        IReadOnlyDictionary<Guid, EpisodeGuideRow> episodes,
        out EpisodeGuideRow episode)
    {
        episode = null!;
        if (!item.JellyfinItemId.HasValue || !episodes.TryGetValue(item.JellyfinItemId.Value, out episode!))
        {
            return false;
        }

        if (episode.SeriesId is null || episode.SeriesId == Guid.Empty)
        {
            return false;
        }

        var played = item.Finish - item.Start;
        var duration = played > TimeSpan.Zero ? played : episode.Runtime ?? TimeSpan.Zero;
        if (episode.Runtime is TimeSpan catalog && catalog > TimeSpan.Zero && catalog < duration)
        {
            duration = catalog;
        }

        return ShortEpisodeBlocks.IsShortRuntime(duration);
    }

    private static TvGuideBlockEpisode ToBlockEpisode(EpisodeGuideRow episode)
    {
        var onScreen = GuideMetadataService.FormatOnScreen(episode.SeasonNumber, episode.EpisodeNumber);
        return new TvGuideBlockEpisode
        {
            Title = string.IsNullOrWhiteSpace(episode.Name) ? "Episode" : episode.Name,
            Episode = string.IsNullOrWhiteSpace(onScreen) ? null : onScreen,
            Description = string.IsNullOrWhiteSpace(episode.Plot) ? null : episode.Plot.Trim()
        };
    }

    private static string CombineShortEpisodeDescription(ShortEpisodeBlock block)
    {
        var sb = new StringBuilder();
        foreach (var part in block.Parts)
        {
            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            sb.Append(part.Title);
            if (!string.IsNullOrWhiteSpace(part.Episode))
            {
                sb.Append(" (").Append(part.Episode).Append(')');
            }

            if (!string.IsNullOrWhiteSpace(part.Description))
            {
                sb.Append('\n').Append(part.Description);
            }
        }

        var text = sb.ToString();
        return text.Length <= 1500 ? text : text[..1497] + "...";
    }

    /// <summary>
    /// Extends each programme to the next :00/:30 so commercial filler does not leave a hole
    /// in the in-app guide or Jellyfin XMLTV.
    /// </summary>
    private static void PadProgramsToHalfHour(List<PlayoutItem> items, TimeZoneInfo tz)
    {
        foreach (var group in items.GroupBy(item => item.ChannelId))
        {
            var ordered = group.OrderBy(item => item.Start).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var item = ordered[i];
                if (IsMusicVideoHourBlock(item))
                {
                    continue;
                }

                var padded = ScheduleTimeZoneHelper.CeilToHalfHourUtc(item.Finish, tz);
                if (padded <= item.Finish)
                {
                    continue;
                }

                var nextStart = i + 1 < ordered.Count ? ordered[i + 1].Start : (DateTime?)null;
                if (nextStart is DateTime next && next > item.Finish)
                {
                    item.Finish = next < padded ? next : padded;
                }
                else if (nextStart is null)
                {
                    item.Finish = padded;
                }
            }
        }
    }

    /// <summary>
    /// Music-video channels list as hour-long blocks of the channel name.
    /// Clip titles, artists, and posters stay off the guide; playout is unchanged.
    /// </summary>
    private static List<PlayoutItem> CollapseMusicVideoHours(
        List<PlayoutItem> items,
        IReadOnlyDictionary<Guid, Channel> channels,
        TimeZoneInfo tz)
    {
        var musicVideoIds = channels.Values
            .Where(channel => channel.ContentType == ChannelContentType.MusicVideo)
            .Select(channel => channel.Id)
            .ToHashSet();
        if (musicVideoIds.Count == 0)
        {
            return items;
        }

        var result = new List<PlayoutItem>(items.Count);
        foreach (var item in items)
        {
            if (!musicVideoIds.Contains(item.ChannelId))
            {
                result.Add(item);
            }
        }

        foreach (var channelId in musicVideoIds)
        {
            if (!channels.TryGetValue(channelId, out var channel))
            {
                continue;
            }

            var channelItems = items.Where(item => item.ChannelId == channelId).ToList();
            if (channelItems.Count == 0)
            {
                continue;
            }

            var minStart = channelItems.Min(item => AsUtc(item.Start));
            var maxFinish = channelItems.Max(item => AsUtc(item.Finish));
            var hour = ScheduleTimeZoneHelper.FloorToHourUtc(minStart, tz);
            var guard = 0;
            while (hour < maxFinish && guard++ < 24 * (PlayoutScheduleHelper.MaxPlayoutDays + 2))
            {
                var hourEnd = ScheduleTimeZoneHelper.AddLocalHoursUtc(hour, 1, tz);
                if (hourEnd <= hour)
                {
                    break;
                }

                result.Add(new PlayoutItem
                {
                    ChannelId = channelId,
                    Start = hour,
                    Finish = hourEnd,
                    Title = channel.Name,
                    GuideGroup = MusicVideoHourGuideGroup
                });
                hour = hourEnd;
            }
        }

        return result;
    }

    private static bool IsMusicVideoHourBlock(PlayoutItem item)
        => string.Equals(item.GuideGroup, MusicVideoHourGuideGroup, StringComparison.Ordinal);

    private XElement BuildMusicVideoHourProgramme(PlayoutItem item, Channel channel, string baseUrl)
    {
        var programme = new XElement(
            "programme",
            new XAttribute("start", FormatXmlTvDate(item.Start)),
            new XAttribute("stop", FormatXmlTvDate(item.Finish)),
            new XAttribute("channel", item.ChannelId.ToString("N")),
            CreateLangElement("title", channel.Name, null),
            CreateLangElement("category", "Music Video", null));

        var logoUrl = GetLogoUrl(channel, baseUrl);
        if (!string.IsNullOrWhiteSpace(logoUrl))
        {
            programme.Add(new XElement("icon", new XAttribute("src", logoUrl)));
        }

        return programme;
    }

    private async Task<XDocument> BuildXmlTvDocumentAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var channels = await _db.Channels.Where(c => c.Enabled).OrderBy(c => c.Number).AsNoTracking().ToListAsync(cancellationToken);
        var start = DateTime.UtcNow.AddHours(-3);
        var end = DateTime.UtcNow.AddDays(PlayoutScheduleHelper.GetPlayoutDaysToBuild());

        var root = new XElement(
            "tv",
            new XAttribute("generator-info-name", "ChannelFlow-Server"),
            new XAttribute("generator-info-url", "https://github.com/binarygeek119/ChannelFlow"));

        foreach (var channel in channels)
        {
            var channelElement = new XElement(
                "channel",
                new XAttribute("id", channel.Id.ToString("N")),
                new XElement("display-name", channel.Name),
                new XElement("icon", new XAttribute("src", GetLogoUrl(channel, baseUrl))));
            channelElement.Add(new XElement("lcn", ChannelNumbers.Format(channel.Number)));
            root.Add(channelElement);
        }

        var items = await _db.PlayoutItems
            .Where(p =>
                p.Finish > start
                && p.Start < end
                && (p.GuideGroup == null || (p.GuideGroup != "commercial" && p.GuideGroup != LogoBumperService.GuideGroup)))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var channelsById = channels.ToDictionary(c => c.Id);
        var tz = ScheduleTimeZoneHelper.ResolveScheduleTimeZone();
        var prepared = await PrepareGuideItemsAsync(items, channelsById, tz, cancellationToken);
        items = prepared.Items;
        var shortBlocks = prepared.ShortBlocks;

        var metadataByItemId = _guideMetadata.ResolveBatch(items.Select(i => i.JellyfinItemId));
        var weatherItems = items
            .Where(i => i.IsVirtual && i.VirtualSource == VirtualContentSource.WeatherStar)
            .ToList();
        var weatherMetadataByPlayoutId = await _weatherGuideMetadata.ResolveAsync(
            weatherItems,
            channelsById,
            channel => GetLogoUrl(channel, baseUrl),
            cancellationToken);

        foreach (var item in items)
        {
            if (IsMusicVideoHourBlock(item) && channelsById.TryGetValue(item.ChannelId, out var musicChannel))
            {
                root.Add(BuildMusicVideoHourProgramme(item, musicChannel, baseUrl));
                continue;
            }

            GuideProgramMetadata? metadata = null;
            if (weatherMetadataByPlayoutId.TryGetValue(item.Id, out var weatherMetadata))
            {
                metadata = weatherMetadata;
            }
            else if (item.JellyfinItemId.HasValue)
            {
                metadataByItemId.TryGetValue(item.JellyfinItemId.Value, out metadata);
            }

            shortBlocks.TryGetValue(item.Id, out var shortBlock);
            root.Add(BuildProgrammeElement(item, metadata, shortBlock, baseUrl));
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    private XElement BuildProgrammeElement(
        PlayoutItem item,
        GuideProgramMetadata? metadata,
        ShortEpisodeBlock? shortBlock,
        string baseUrl)
    {
        var programme = new XElement(
            "programme",
            new XAttribute("start", FormatXmlTvDate(item.Start)),
            new XAttribute("stop", FormatXmlTvDate(item.Finish)),
            new XAttribute("channel", item.ChannelId.ToString("N")));

        var title = metadata?.Title ?? item.Title;
        var subTitle = metadata?.SubTitle;
        var description = metadata?.Description;
        var episodeXmlTvNs = metadata?.EpisodeXmlTvNs;
        var episodeOnScreen = metadata?.EpisodeOnScreen;
        if (shortBlock is not null && shortBlock.Parts.Count > 0)
        {
            title = shortBlock.SeriesName;
            subTitle = null;
            description = CombineShortEpisodeDescription(shortBlock);
            episodeXmlTvNs = shortBlock.Parts.Count == 1 ? episodeXmlTvNs : null;
            episodeOnScreen = shortBlock.Parts.Count == 1 ? shortBlock.Parts[0].Episode : null;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            programme.Add(CreateLangElement("title", title, metadata?.Language));
        }

        if (!string.IsNullOrWhiteSpace(subTitle))
        {
            programme.Add(CreateLangElement("sub-title", subTitle, metadata?.Language));
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            programme.Add(CreateLangElement("desc", description, metadata?.Language));
        }

        if (metadata?.Categories is not null)
        {
            foreach (var category in metadata.Categories.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                programme.Add(CreateLangElement("category", category, metadata.Language));
            }
        }

        if (!string.IsNullOrWhiteSpace(episodeXmlTvNs))
        {
            programme.Add(new XElement(
                "episode-num",
                new XAttribute("system", "xmltv_ns"),
                episodeXmlTvNs));
        }

        if (!string.IsNullOrWhiteSpace(episodeOnScreen))
        {
            programme.Add(new XElement(
                "episode-num",
                new XAttribute("system", "onscreen"),
                episodeOnScreen));
        }

        if (metadata?.ProductionYear is int year && year > 0)
        {
            programme.Add(new XElement("date", year.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(metadata?.OfficialRating))
        {
            programme.Add(new XElement(
                "rating",
                new XAttribute("system", "MPAA"),
                metadata.OfficialRating));
        }

        var posterUrl = !string.IsNullOrWhiteSpace(metadata?.IconUrl)
            ? metadata.IconUrl
            : _guideMetadata.GetPosterUrlIfAvailable(baseUrl, metadata?.PosterItemId ?? item.JellyfinItemId);
        if (!string.IsNullOrWhiteSpace(posterUrl))
        {
            programme.Add(new XElement("icon", new XAttribute("src", posterUrl)));
        }

        return programme;
    }

    private static XElement CreateLangElement(string name, string value, string? language)
    {
        return string.IsNullOrWhiteSpace(language)
            ? new XElement(name, value)
            : new XElement(name, new XAttribute("lang", language), value);
    }

    private static byte[] SerializeUtf8(XDocument doc)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = true,
            OmitXmlDeclaration = false
        };

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            doc.Save(writer);
        }

        return ms.ToArray();
    }

    public async Task<string> GenerateM3uAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken = default)
    {
        var channels = await _db.Channels.Where(c => c.Enabled).OrderBy(c => c.Number).AsNoTracking().ToListAsync(cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        foreach (var channel in channels)
        {
            sb.Append("#EXTINF:-1 tvg-id=\"")
                .Append(channel.Id.ToString("N"))
                .Append("\" tvg-chno=\"")
                .Append(ChannelNumbers.Format(channel.Number))
                .Append("\" tvg-name=\"")
                .Append(EscapeM3u(channel.Name))
                .Append("\" tvg-logo=\"")
                .Append(GetLogoUrl(channel, baseUrl))
                .Append("\",")
                .AppendLine(channel.Name);

            var nextEnabled = FinTvRuntime.Current?.Configuration.NextTranscoding.Enabled == true;
            var streamUrl = nextEnabled
                ? PluginApiKey.AppendQuery(
                    $"{baseUrl.TrimEnd('/')}/iptv/next/channel/{ChannelNumbers.Format(channel.Number)}.m3u8",
                    apiKey)
                : PluginApiKey.AppendQuery($"{baseUrl.TrimEnd('/')}/iptv/stream/{channel.Id:N}", apiKey);
            sb.AppendLine(streamUrl);
        }

        return sb.ToString();
    }

    public static string GetPublicBaseUrl(HttpRequest? request, IPublicBaseUrl? appHost = null)
    {
        var configured = ReverseProxyHosting.NormalizePublicBaseUrl(FinTvRuntime.Current?.Configuration.PublicBaseUrl);
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = ReverseProxyHosting.NormalizePublicBaseUrl(AppEnvironment.Get("PUBLIC_URL"));
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        if (request is not null && appHost is not null)
        {
            try
            {
                return appHost.GetSmartApiUrl(request).TrimEnd('/');
            }
            catch
            {
                // Fall back to the incoming request (including reverse-proxy headers).
            }
        }

        if (request is not null)
        {
            return ReverseProxyHosting.PublicOrigin(request);
        }

        return "http://127.0.0.1:8097";
    }

    private string GetLogoUrl(Channel channel, string? baseUrl = null)
    {
        baseUrl ??= FinTvRuntime.Current?.Configuration.PublicBaseUrl ?? "http://localhost:8096";
        var fileName = channel.LogoFileName;
        if (_holidays.IsHolidayChannel(channel))
        {
            var scheduleDate = _holidays.GetScheduleDateUtc(DateTime.UtcNow);
            fileName = _holidays.ResolveEffectiveLogoFileName(channel, scheduleDate) ?? fileName;
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return $"{baseUrl.TrimEnd('/')}/api/logos/{channel.Id:N}/{Uri.EscapeDataString(fileName)}";
        }

        return string.Empty;
    }

    private static string FormatXmlTvDate(DateTime utc)
    {
        return utc.ToUniversalTime().ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture)
            .Replace(":", string.Empty);
    }

    private static string EscapeM3u(string value) => value.Replace(',', ' ');
}

internal sealed class PreparedGuideItems
{
    public PreparedGuideItems(List<PlayoutItem> items, Dictionary<Guid, ShortEpisodeBlock> shortBlocks)
    {
        Items = items;
        ShortBlocks = shortBlocks;
    }

    public List<PlayoutItem> Items { get; }

    public Dictionary<Guid, ShortEpisodeBlock> ShortBlocks { get; }
}

internal sealed class ShortEpisodeBlock
{
    public string SeriesName { get; set; } = string.Empty;

    public List<TvGuideBlockEpisode> Parts { get; set; } = [];
}

internal sealed class EpisodeGuideRow
{
    public Guid Id { get; init; }

    public Guid? SeriesId { get; init; }

    public string? SeriesName { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Plot { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public TimeSpan? Runtime { get; init; }
}
