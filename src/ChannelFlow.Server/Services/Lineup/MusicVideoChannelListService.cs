using System.Text.Json;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Artist lists and YouTube playlist sources for The Parody Channel, Rap On Tap, and HeadPhone Jack.
/// </summary>
public sealed class MusicVideoChannelListService
{
    public const string ParodyTag = "channelflow-parody";
    public const string RapTag = "channelflow-rap";
    public const string HeadphoneTag = "channelflow-music-video";

    private readonly FinTvDbContext _db;
    private readonly YouTubeCommercialStreamService _youtube;
    private readonly ILogger<MusicVideoChannelListService> _logger;

    public MusicVideoChannelListService(
        FinTvDbContext db,
        YouTubeCommercialStreamService youtube,
        ILogger<MusicVideoChannelListService> logger)
    {
        _db = db;
        _youtube = youtube;
        _logger = logger;
    }

    public async Task<List<Channel>> ListMusicVideoChannelsAsync(CancellationToken cancellationToken)
        => await _db.Channels.AsNoTracking()
            .Where(c => c.ContentType == ChannelContentType.MusicVideo)
            .OrderBy(c => c.Number)
            .ToListAsync(cancellationToken);

    public async Task<List<MusicVideoChannelArtist>> ListArtistsAsync(Guid channelId, CancellationToken cancellationToken)
        => await _db.MusicVideoChannelArtists.AsNoTracking()
            .Where(row => row.ChannelId == channelId)
            .OrderBy(row => row.ArtistName)
            .ToListAsync(cancellationToken);

    public async Task<MusicVideoChannelArtist> AddArtistAsync(Guid channelId, string artistName, CancellationToken cancellationToken)
    {
        var name = NormalizeArtist(artistName);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Artist name is required.");
        }

        var existing = await _db.MusicVideoChannelArtists
            .FirstOrDefaultAsync(row => row.ChannelId == channelId && row.ArtistName.ToLower() == name.ToLower(), cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var row = new MusicVideoChannelArtist
        {
            ChannelId = channelId,
            ArtistName = name
        };
        _db.MusicVideoChannelArtists.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return row;
    }

    public async Task<bool> RemoveArtistAsync(Guid channelId, Guid artistId, CancellationToken cancellationToken)
    {
        var row = await _db.MusicVideoChannelArtists
            .FirstOrDefaultAsync(a => a.Id == artistId && a.ChannelId == channelId, cancellationToken);
        if (row is null)
        {
            return false;
        }

        _db.MusicVideoChannelArtists.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<List<MusicVideoYoutubeSource>> ListYoutubeSourcesAsync(Guid channelId, CancellationToken cancellationToken)
        => await _db.MusicVideoYoutubeSources.AsNoTracking()
            .Where(row => row.ChannelId == channelId && row.ParentSourceId == null)
            .OrderByDescending(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<MusicVideoYoutubeSource> AddYoutubeSourceAsync(Guid channelId, string url, CancellationToken cancellationToken)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("A YouTube video or playlist URL is required.");
        }

        var entries = await _youtube.ListVideosAsync(trimmed, cancellationToken);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("Could not resolve that YouTube URL. Check cookies on the YouTube tab and try again.");
        }

        var isPlaylist = entries.Count > 1 || trimmed.Contains("list=", StringComparison.OrdinalIgnoreCase);
        var parent = new MusicVideoYoutubeSource
        {
            ChannelId = channelId,
            SourceUrl = trimmed,
            IsPlaylist = isPlaylist,
            Title = isPlaylist ? $"Playlist ({entries.Count} videos)" : entries[0].Title,
            Artist = entries[0].Artist,
            YoutubeVideoId = isPlaylist ? null : entries[0].VideoId,
            DurationSeconds = isPlaylist ? null : entries[0].DurationSeconds
        };
        _db.MusicVideoYoutubeSources.Add(parent);
        await _db.SaveChangesAsync(cancellationToken);

        if (isPlaylist)
        {
            foreach (var entry in entries.Take(300))
            {
                _db.MusicVideoYoutubeSources.Add(new MusicVideoYoutubeSource
                {
                    ChannelId = channelId,
                    SourceUrl = YouTubeUrlHelper.WatchUrl(entry.VideoId),
                    YoutubeVideoId = entry.VideoId,
                    Title = entry.Title,
                    Artist = entry.Artist,
                    DurationSeconds = entry.DurationSeconds,
                    IsPlaylist = false,
                    ParentSourceId = parent.Id
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation("Imported {Count} YouTube music video(s) for channel {ChannelId}", entries.Count, channelId);
        return parent;
    }

    public async Task<bool> RemoveYoutubeSourceAsync(Guid channelId, Guid sourceId, CancellationToken cancellationToken)
    {
        var children = await _db.MusicVideoYoutubeSources
            .Where(row => row.ChannelId == channelId && row.ParentSourceId == sourceId)
            .ToListAsync(cancellationToken);
        var row = await _db.MusicVideoYoutubeSources
            .FirstOrDefaultAsync(s => s.Id == sourceId && s.ChannelId == channelId, cancellationToken);
        if (row is null && children.Count == 0)
        {
            return false;
        }

        if (children.Count > 0)
        {
            _db.MusicVideoYoutubeSources.RemoveRange(children);
        }

        if (row is not null)
        {
            _db.MusicVideoYoutubeSources.Remove(row);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public HashSet<string>? GetAllowedArtistNames(Channel channel)
    {
        var tag = ChannelAiRules.ExtractLibraryTag(channel.FilterJson);
        if (channel.ContentType != ChannelContentType.MusicVideo)
        {
            return null;
        }

        var named = LoadArtistNames(channel.Id);
        if (FilterDefinition.PresetIdsEqual(tag, ParodyTag) || FilterDefinition.PresetIdsEqual(tag, RapTag))
        {
            return named;
        }

        if (!FilterDefinition.PresetIdsEqual(tag, HeadphoneTag))
        {
            return named.Count > 0 ? named : null;
        }

        if (named.Count > 0)
        {
            return named;
        }

        var otherChannelIds = _db.Channels.AsNoTracking()
            .Where(c => c.ContentType == ChannelContentType.MusicVideo && c.Id != channel.Id)
            .Select(c => c.Id)
            .ToList();
        var excluded = _db.MusicVideoChannelArtists.AsNoTracking()
            .Where(row => otherChannelIds.Contains(row.ChannelId))
            .Select(row => row.ArtistName)
            .ToList()
            .Select(NormalizeArtist)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return LoadCatalogArtistNames()
            .Where(name => !excluded.Any(ex => ArtistsMatch(name, ex)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool ArtistIsAllowed(Channel channel, string? artist)
    {
        var allowed = GetAllowedArtistNames(channel);
        if (allowed is null)
        {
            return true;
        }

        if (allowed.Count == 0)
        {
            return false;
        }

        var name = NormalizeArtist(artist);
        return allowed.Any(entry => ArtistsMatch(name, entry));
    }

    public async Task<MusicVideoPick?> PickNextAsync(
        Channel channel,
        PlayoutAnchorState anchor,
        IReadOnlyList<ResolvedCandidate> catalogPicks,
        CancellationToken cancellationToken)
    {
        var youtube = await LoadPlayableYoutubeAsync(channel.Id, cancellationToken);
        var catalog = catalogPicks
            .Select(item => new MusicVideoPick
            {
                JellyfinItemId = item.JellyfinItemId,
                Title = item.Title,
                Artist = item.Artist ?? string.Empty,
                Duration = item.Duration > TimeSpan.Zero ? item.Duration : TimeSpan.FromMinutes(4)
            })
            .ToList();
        var combined = catalog
            .Concat(youtube)
            .Where(pick => !string.IsNullOrWhiteSpace(pick.Title))
            .ToList();
        if (combined.Count == 0)
        {
            return null;
        }

        var recent = anchor.RecentMusicVideoArtists;
        var lastArtist = recent.Count > 0 ? recent[^1] : null;
        var rng = new Random(HashCode.Combine(channel.PlayoutSeed, recent.Count, combined.Count));
        var ranked = combined
            .Select(pick =>
            {
                var artist = pick.Artist;
                var distance = DistanceSinceArtist(recent, artist);
                var penalty = string.Equals(artist, lastArtist, StringComparison.OrdinalIgnoreCase) ? -10_000 : 0;
                return (pick, score: distance + penalty, jitter: rng.Next());
            })
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.jitter)
            .ToList();

        var chosen = ranked[0].pick;
        if (!string.IsNullOrWhiteSpace(chosen.Artist))
        {
            recent.Add(chosen.Artist);
            if (recent.Count > 64)
            {
                recent.RemoveRange(0, recent.Count - 64);
            }
        }

        return chosen;
    }

    private async Task<List<MusicVideoPick>> LoadPlayableYoutubeAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var rows = await _db.MusicVideoYoutubeSources.AsNoTracking()
            .Where(row => row.ChannelId == channelId && !row.IsPlaylist && row.YoutubeVideoId != null)
            .ToListAsync(cancellationToken);
        return rows.Select(row => new MusicVideoPick
        {
            ExternalUrl = string.IsNullOrWhiteSpace(row.SourceUrl)
                ? YouTubeUrlHelper.WatchUrl(row.YoutubeVideoId)
                : row.SourceUrl,
            YoutubeVideoId = row.YoutubeVideoId,
            Title = string.IsNullOrWhiteSpace(row.Title) ? "YouTube music video" : row.Title,
            Artist = row.Artist ?? string.Empty,
            Duration = TimeSpan.FromSeconds(Math.Clamp(row.DurationSeconds ?? 240, 30, 900))
        }).ToList();
    }

    private HashSet<string> LoadArtistNames(Guid channelId)
        => _db.MusicVideoChannelArtists.AsNoTracking()
            .Where(row => row.ChannelId == channelId)
            .Select(row => row.ArtistName)
            .ToList()
            .Select(NormalizeArtist)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> LoadCatalogArtistNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jsonRows = _db.MusicVideos.AsNoTracking()
            .Where(row => !row.IsMissing)
            .Select(row => row.ArtistsJson)
            .ToList();
        foreach (var json in jsonRows)
        {
            foreach (var name in ReadArtistNames(json))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static IEnumerable<string> ReadArtistNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json is "[]")
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var names = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.TryGetProperty("Name", out var named) ? named.GetString() : null;
                name = NormalizeArtist(name);
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int DistanceSinceArtist(List<string> recent, string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return recent.Count + 1;
        }

        for (var i = recent.Count - 1; i >= 0; i--)
        {
            if (string.Equals(recent[i], artist, StringComparison.OrdinalIgnoreCase))
            {
                return recent.Count - 1 - i;
            }
        }

        return recent.Count + 8;
    }

    public static bool ArtistsMatch(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeArtist(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}

public sealed class MusicVideoPick
{
    public Guid? JellyfinItemId { get; init; }

    public string? ExternalUrl { get; init; }

    public string? YoutubeVideoId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }
}
