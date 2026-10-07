using System.Text.Json;
using FinTv;
using FinTv.Auth;
using FinTv.Data;
using FinTv.Domain;
using FinTv.News;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Api;

[ApiController]
[Route("api/plugin")]
public class PluginBridgeController : ControllerBase
{
    private readonly FinTvDbContext _db;
    private readonly CatalogTypedStore _typedCatalog;
    private readonly CatalogCleanupService _catalogCleanup;
    private readonly CatalogChapterProbeService _chapters;

    public PluginBridgeController(
        FinTvDbContext db,
        CatalogTypedStore typedCatalog,
        CatalogCleanupService catalogCleanup,
        CatalogChapterProbeService chapters)
    {
        _db = db;
        _typedCatalog = typedCatalog;
        _catalogCleanup = catalogCleanup;
        _chapters = chapters;
    }

    [HttpPost("catalog/sync/begin")]
    public IActionResult BeginCatalogSync()
    {
        _catalogCleanup.BeginCatalogSync();
        return Ok(new { startedAt = FinTvRuntime.Current?.Configuration.CatalogCleanup.LastCatalogSyncStartedAt });
    }

    [HttpPost("catalog/sync/complete")]
    public async Task<IActionResult> CompleteCatalogSync(CancellationToken cancellationToken)
    {
        var marked = await _catalogCleanup.CompleteCatalogSyncAsync(cancellationToken);
        return Ok(new { markedMissing = marked });
    }

    [HttpPost("catalog")]
    public async Task<IActionResult> SyncCatalog(
        [FromBody] CatalogSyncRequest request,
        [FromServices] GuideUpdateTracker guideUpdates,
        CancellationToken cancellationToken)
    {
        if (request.Items is null)
        {
            return BadRequest(new { message = "Items are required." });
        }

        var incomingIds = request.Items.Select(i => i.Id).ToHashSet();

        await _db.MediaChapters
            .Where(chapter => incomingIds.Contains(chapter.MediaItemId))
            .ExecuteDeleteAsync(cancellationToken);
        foreach (var tracked in _db.ChangeTracker.Entries<MediaChapter>()
            .Where(entry => incomingIds.Contains(entry.Entity.MediaItemId))
            .ToList())
        {
            tracked.State = EntityState.Detached;
        }

        foreach (var item in request.Items)
        {
            var row = await _db.MediaItems.FirstOrDefaultAsync(i => i.Id == item.Id, cancellationToken);
            if (row is null)
            {
                row = new MediaItem { Id = item.Id };
                _db.MediaItems.Add(row);
            }

            row.Name = item.Name ?? string.Empty;
            row.SortName = item.SortName;
            row.Overview = string.IsNullOrWhiteSpace(item.Overview) ? item.Plot : item.Overview;
            row.Kind = item.Kind;
            row.Path = string.IsNullOrWhiteSpace(item.Path) ? item.JellyfinPath : item.Path;
            row.ParentId = item.ParentId;
            row.SeriesId = item.SeriesId;
            row.SeriesName = item.SeriesName;
            row.ProductionYear = item.ProductionYear;
            row.PremiereDate = item.PremiereDate;
            row.OfficialRating = item.OfficialRating;
            row.CommunityRating = item.CommunityRating;
            row.CriticRating = item.CriticRating;
            row.RuntimeTicks = item.RuntimeTicks;
            row.Runtime = string.IsNullOrWhiteSpace(item.Runtime) ? FormatRuntime(item.RuntimeTicks) : item.Runtime;
            row.IndexNumber = item.IndexNumber;
            row.ParentIndexNumber = item.ParentIndexNumber;
            row.LibraryId = item.LibraryId;
            row.LibraryName = item.LibraryName;
            row.CollectionType = item.CollectionType;
            row.PrimaryImagePath = item.PrimaryImagePath;
            row.Album = item.Album;
            row.MediaType = item.MediaType;
            row.SeasonId = item.SeasonId;
            row.SeasonName = item.SeasonName;
            row.GenresJson = JsonSerializer.Serialize(item.Genres ?? []);
            row.TagsJson = JsonSerializer.Serialize(item.Tags ?? []);
            row.StudiosJson = JsonSerializer.Serialize(item.Studios ?? []);
            row.CollectionNamesJson = JsonSerializer.Serialize(item.CollectionNames ?? []);
            row.PeopleJson = JsonSerializer.Serialize(
                item.People is { Count: > 0 }
                    ? item.People
                    : (item.Stars ?? []).Select(name => new CatalogPersonDto { Name = name, Type = "Actor" }));
            row.ProviderIdsJson = JsonSerializer.Serialize(item.ProviderIds ?? new Dictionary<string, string>());
            row.ArtistsJson = JsonSerializer.Serialize(item.Artists ?? []);
            row.AlbumArtistsJson = JsonSerializer.Serialize(item.AlbumArtists ?? []);
            row.Width = item.Width;
            row.Height = item.Height;
            row.AspectRatio = VideoAspectFormat.ForCatalog(row.TrueAspectRatio, item.AspectRatio, item.Width, item.Height);
            row.SyncedAt = DateTime.UtcNow;
            row.IsMissing = false;
            row.MissingSince = null;

            if (item.Chapters is { Count: > 0 })
            {
                foreach (var chapter in item.Chapters)
                {
                    row.Chapters.Add(new MediaChapter
                    {
                        MediaItemId = row.Id,
                        StartPositionTicks = chapter.StartPositionTicks,
                        Name = chapter.Name
                    });
                }
            }
        }

        await _typedCatalog.UpsertAsync(request.Items, request.ReplaceAll, cancellationToken);
        await _db.SaveChangesIgnoringGoneRowsAsync(cancellationToken);
        try
        {
            await _chapters.ProbeAsync(incomingIds, missingOnly: false, onProgress: null, cancellationToken);
        }
        catch (Exception)
        {
            // Catalog import already saved; chapter probe is best-effort.
        }
        if (request.ReplaceAll)
        {
            await _catalogCleanup.MarkMissingExceptAsync(incomingIds, cancellationToken);
        }

        if (request.Libraries is { Count: > 0 })
        {
            SaveReportedLibraries(request.Libraries);
        }

        guideUpdates.RegisterPlugin(Request, null);
        return Ok(new { count = request.Items.Count });
    }

    /// <summary>
    /// Replaces the Jellyfin library list used by the ChannelFlow Library tab and catalog sync filters.
    /// </summary>
    [HttpPost("libraries")]
    public IActionResult SyncLibraries(
        [FromBody] JellyfinLibraryListRequest? request,
        [FromServices] GuideUpdateTracker guideUpdates)
    {
        var libraries = SaveReportedLibraries(request?.Libraries);
        guideUpdates.RegisterPlugin(Request, null);
        return Ok(new { count = libraries.Count });
    }

    /// <summary>
    /// Library IDs the Jellyfin plugin should include when syncing catalog.
    /// Empty lists mean sync every library of that type.
    /// </summary>
    [HttpGet("library-sync")]
    public ActionResult<object> GetLibrarySync()
    {
        var settings = FinTvRuntime.Current?.Configuration.JellyfinLibraries ?? new Configuration.JellyfinLibrarySettings();
        return Ok(new
        {
            tvLibraryIds = settings.TvLibraryIds,
            movieLibraryIds = settings.MovieLibraryIds,
            musicLibraryIds = settings.MusicLibraryIds,
            musicVideoLibraryIds = settings.MusicVideoLibraryIds,
            homeVideoLibraryIds = settings.HomeVideoLibraryIds,
            libraries = settings.Libraries
        });
    }

    [HttpPatch("catalog/{itemId:guid}/chapters")]
    public async Task<IActionResult> PatchChapters(
        Guid itemId,
        [FromBody] List<CatalogChapterDto> chapters,
        CancellationToken cancellationToken)
    {
        var row = await _db.MediaItems.FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        await _db.MediaChapters.Where(c => c.MediaItemId == itemId).ExecuteDeleteAsync(cancellationToken);
        foreach (var tracked in _db.ChangeTracker.Entries<MediaChapter>()
            .Where(entry => entry.Entity.MediaItemId == itemId)
            .ToList())
        {
            tracked.State = EntityState.Detached;
        }

        row.Chapters.Clear();
        foreach (var chapter in chapters ?? [])
        {
            row.Chapters.Add(new MediaChapter
            {
                MediaItemId = row.Id,
                StartPositionTicks = chapter.StartPositionTicks,
                Name = chapter.Name
            });
        }

        await _db.SaveChangesIgnoringGoneRowsAsync(cancellationToken);
        return Ok(new { count = row.Chapters.Count });
    }

    [HttpGet("live-tv-urls")]
    public ActionResult<object> LiveTvUrls()
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request);
        return Ok(new
        {
            m3u = PluginApiKey.AppendQuery($"{baseUrl}/iptv/channels.m3u"),
            epg = PluginApiKey.AppendQuery($"{baseUrl}/iptv/epg.xml")
        });
    }

    [HttpGet("guide-status")]
    public ActionResult<object> GuideStatus([FromServices] GuideUpdateTracker guideUpdates)
    {
        var status = guideUpdates.Snapshot();
        return Ok(new
        {
            revision = status.Revision,
            updatedAt = status.UpdatedAt
        });
    }

    /// <summary>
    /// Plugin handshake so ChannelFlow-Server can push a Live TV guide refresh after playout changes.
    /// </summary>
    [HttpPost("register")]
    public ActionResult<object> Register(
        [FromBody] PluginRegisterRequest? request,
        [FromServices] GuideUpdateTracker guideUpdates)
    {
        var jellyfinUrl = guideUpdates.RegisterPlugin(Request, request?.JellyfinUrl);
        return Ok(new { jellyfinUrl });
    }

    internal static List<Configuration.JellyfinLibraryInfo> SaveReportedLibraries(
        IEnumerable<JellyfinLibraryDto>? libraries)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return [];
        }

        var normalized = (libraries ?? [])
            .Where(library => library.Id != Guid.Empty && !string.IsNullOrWhiteSpace(library.Name))
            .GroupBy(library => library.Id)
            .Select(group => group.First())
            .Select(library => new Configuration.JellyfinLibraryInfo
            {
                Id = library.Id,
                Name = library.Name!.Trim(),
                CollectionType = string.IsNullOrWhiteSpace(library.CollectionType)
                    ? null
                    : library.CollectionType.Trim()
            })
            .ToList();

        plugin.Configuration.JellyfinLibraries.Libraries = normalized;
        plugin.SaveConfiguration();
        return normalized;
    }

    private static string? FormatRuntime(long? ticks)
    {
        if (ticks is not > 0)
        {
            return null;
        }

        var time = TimeSpan.FromTicks(ticks.Value);
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}h {time.Minutes:00}m";
        }

        if (time.TotalMinutes >= 1)
        {
            return $"{(int)time.TotalMinutes}m {time.Seconds:00}s";
        }

        return $"{time.Seconds}s";
    }
}

[ApiController]
[Route("api/settings")]
[Authorize(Policy = "admin")]
public class PathMappingController : ControllerBase
{
    private readonly PathRemapService _remap;

    public PathMappingController(PathRemapService remap)
    {
        _remap = remap;
    }

    [HttpGet("path-mappings")]
    public async Task<ActionResult<object>> Get(CancellationToken cancellationToken)
        => Ok(await _remap.GetAllAsync(cancellationToken));

    [HttpPut("path-mappings")]
    public async Task<IActionResult> Put([FromBody] List<PathMapping> mappings, CancellationToken cancellationToken)
    {
        await _remap.ReplaceAllAsync(mappings ?? [], cancellationToken);
        return Ok(await _remap.GetAllAsync(cancellationToken));
    }

    [HttpPost("path-mappings/test")]
    public async Task<ActionResult<object>> Test([FromQuery] int sample = 50, CancellationToken cancellationToken = default)
        => Ok(await _remap.TestAsync(sample, cancellationToken));
}

[ApiController]
[Route("api/news")]
[Authorize(Policy = "admin")]
public class NewsController : ControllerBase
{
    private readonly FinTvDbContext _db;
    private readonly JellyfinCatalogService _catalog;
    private readonly NewsHeadlineService _headlines;
    private readonly NewsBulletinService _bulletins;

    public NewsController(
        FinTvDbContext db,
        JellyfinCatalogService catalog,
        NewsHeadlineService headlines,
        NewsBulletinService bulletins)
    {
        _db = db;
        _catalog = catalog;
        _headlines = headlines;
        _bulletins = bulletins;
    }

    [HttpGet("feeds")]
    public async Task<ActionResult<object>> GetFeeds(CancellationToken cancellationToken)
        => Ok(await _db.NewsFeeds.AsNoTracking().OrderBy(f => f.SortOrder).ToListAsync(cancellationToken));

    [HttpPut("feeds")]
    public async Task<IActionResult> PutFeeds([FromBody] List<NewsFeed> feeds, CancellationToken cancellationToken)
    {
        var existing = await _db.NewsFeeds.ToListAsync(cancellationToken);
        _db.NewsFeeds.RemoveRange(existing);
        var order = 0;
        foreach (var feed in feeds ?? [])
        {
            if (string.IsNullOrWhiteSpace(feed.Url))
            {
                continue;
            }

            _db.NewsFeeds.Add(new NewsFeed
            {
                Url = feed.Url.Trim(),
                Name = feed.Name,
                Enabled = feed.Enabled,
                SortOrder = order++
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(await _db.NewsFeeds.AsNoTracking().OrderBy(f => f.SortOrder).ToListAsync(cancellationToken));
    }

    [HttpGet("settings")]
    public async Task<ActionResult<object>> GetSettings(CancellationToken cancellationToken)
    {
        var settings = await _db.NewsSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken)
            ?? new NewsSettings();
        return Ok(new
        {
            HeaderText = NewsShowWriter.ResolveShowName(settings.HeaderText),
            settings.ArticleCount,
            settings.TtsEnabled,
            settings.TtsEngine,
            settings.AiRewrite,
            settings.Voice,
            settings.MusicLibraryId,
            settings.MusicLibraryName,
            settings.ShowHeader,
            settings.ReadHeadlinesOnly,
            settings.IntroText,
            settings.OutroText,
            settings.RefreshMinutes,
            minNewStories = NewsBulletinService.ClampMin(settings.MinNewStories),
            bulletinVideosEnabled = settings.BulletinVideosEnabled,
            bulletin = _bulletins.DescribeStatus(settings),
            musicLibraries = _catalog.GetMusicLibraries().Select(l => new { id = l.Id, name = l.Name })
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> PutSettings([FromBody] NewsSettings settings, CancellationToken cancellationToken)
    {
        var row = await _db.NewsSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new NewsSettings();
            _db.NewsSettings.Add(row);
        }

        row.HeaderText = NewsShowWriter.ResolveShowName(settings.HeaderText);
        row.ArticleCount = Math.Clamp(settings.ArticleCount, 1, 30);
        row.TtsEnabled = settings.TtsEnabled;
        row.TtsEngine = string.Equals(settings.TtsEngine?.Trim(), "ai", StringComparison.OrdinalIgnoreCase)
            ? "ai"
            : "google";
        row.AiRewrite = settings.AiRewrite;
        row.Voice = string.IsNullOrWhiteSpace(settings.Voice) ? "en-US" : settings.Voice.Trim();
        if (NewsChannelService.IsNoMusic(settings)
            || string.Equals(settings.MusicLibraryId?.Trim(), NewsChannelService.NoMusicLibraryId, StringComparison.OrdinalIgnoreCase))
        {
            row.MusicLibraryId = NewsChannelService.NoMusicLibraryId;
            row.MusicLibraryName = "None";
        }
        else
        {
            row.MusicLibraryId = string.IsNullOrWhiteSpace(settings.MusicLibraryId) ? null : settings.MusicLibraryId.Trim();
            row.MusicLibraryName = string.IsNullOrWhiteSpace(settings.MusicLibraryName) ? null : settings.MusicLibraryName.Trim();
        }
        row.ShowHeader = settings.ShowHeader;
        row.ReadHeadlinesOnly = settings.ReadHeadlinesOnly;
        row.IntroText = settings.IntroText;
        row.OutroText = settings.OutroText;
        row.RefreshMinutes = Math.Clamp(settings.RefreshMinutes <= 0 ? 10 : settings.RefreshMinutes, 2, 120);
        row.MinNewStories = NewsBulletinService.ClampMin(settings.MinNewStories);
        row.BulletinVideosEnabled = settings.BulletinVideosEnabled;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(row);
    }

    [HttpPost("bulletins/run")]
    public ActionResult<object> RunBulletin()
    {
        var started = _bulletins.TryQueue();
        return Accepted(new
        {
            started,
            alreadyRunning = !started,
            bulletin = _bulletins.DescribeStatus()
        });
    }

    [HttpPost("bulletins/cleanup")]
    public ActionResult<object> CleanupBulletins()
        => Ok(_bulletins.SweepNow());

    [HttpGet("preview")]
    public async Task<ActionResult<object>> Preview([FromQuery] bool force, CancellationToken cancellationToken)
    {
        var articles = await _headlines.GetAsync(force, cancellationToken);
        return Ok(new
        {
            fetchedAt = _headlines.FetchedAt == DateTime.MinValue ? (DateTime?)null : _headlines.FetchedAt,
            articles
        });
    }
}

public class CatalogSyncRequest
{
    public bool ReplaceAll { get; set; }

    public List<CatalogItemDto> Items { get; set; } = [];

    public List<JellyfinLibraryDto>? Libraries { get; set; }
}

public class PluginRegisterRequest
{
    public string? JellyfinUrl { get; set; }
}

public class JellyfinLibraryListRequest
{
    public List<JellyfinLibraryDto>? Libraries { get; set; }
}

public class JellyfinLibraryDto
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public string? CollectionType { get; set; }
}

public class CatalogItemDto
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public string? SortName { get; set; }

    public string? Overview { get; set; }

    public BaseItemKind Kind { get; set; }

    public string? Path { get; set; }

    public Guid? ParentId { get; set; }

    public Guid? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public int? ProductionYear { get; set; }

    public DateTime? PremiereDate { get; set; }

    public string? OfficialRating { get; set; }

    public float? CommunityRating { get; set; }

    public float? CriticRating { get; set; }

    public string? CustomRating { get; set; }

    public long? RuntimeTicks { get; set; }

    public string? Runtime { get; set; }

    public int? IndexNumber { get; set; }

    public int? ParentIndexNumber { get; set; }

    public Guid? LibraryId { get; set; }

    public Guid? SourceConnectionId { get; set; }

    public string? LibraryName { get; set; }

    public string? CollectionType { get; set; }

    public string? PrimaryImagePath { get; set; }

    public string? Album { get; set; }

    public string? MediaType { get; set; }

    public Guid? SeasonId { get; set; }

    public string? SeasonName { get; set; }

    public Guid? JellyfinId { get; set; }

    public string? JellyfinPath { get; set; }

    public string? Plot { get; set; }

    public List<string>? Genres { get; set; }

    public List<string>? Tags { get; set; }

    public List<string>? Studios { get; set; }

    public List<string>? CollectionNames { get; set; }

    public List<string>? Artists { get; set; }

    public List<string>? AlbumArtists { get; set; }

    public List<string>? Stars { get; set; }

    public List<CatalogPersonDto>? People { get; set; }

    public Dictionary<string, string>? ProviderIds { get; set; }

    public List<CatalogChapterDto>? Chapters { get; set; }

    public string? Format { get; set; }

    public string? Container { get; set; }

    public string? VideoCodec { get; set; }

    public string? AudioCodec { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? AspectRatio { get; set; }

    public string? AlbumArtist { get; set; }
}

public class CatalogPersonDto
{
    public string? Name { get; set; }

    public string? Role { get; set; }

    public string? Type { get; set; }
}

public class CatalogChapterDto
{
    public long StartPositionTicks { get; set; }

    public string? Name { get; set; }
}
