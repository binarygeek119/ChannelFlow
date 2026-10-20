using ChannelFlow.Database;
using ChannelFlow.PluginSdk;
using SdkConnection = ChannelFlow.PluginSdk.Connection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ChannelFlow.Core.Controllers;

public record JellyfinSyncRequest(string Url, string ApiKey, string? LibraryId = null, string ConnectionId = "jellyfin-1");
public record SyncLibrariesRequest(int ConnectionId, List<string> LibraryIds, Dictionary<string, string>? MediaTypes = null);

[ApiController]
[Route("api/media")]
public class MediaController : ControllerBase
{
    private readonly PluginManager _pm;
    private readonly ICoreData _core;
    private readonly IPluginStorage _storage;
    private readonly IPluginLogger _logger;
    private readonly ChannelFlowDbContext _db;
    private readonly TaskTracker _tasks;
    public MediaController(PluginManager pm, ICoreData core, ChannelFlowDbContext db, TaskTracker tasks)
    {
        _pm = pm; _core = core; _db = db; _tasks = tasks;
        _storage = new InMemoryPluginStorage();
        _logger = new ConsolePluginLogger("media");
    }

    [HttpPost("test-jellyfin")]
    public async Task<IActionResult> TestJellyfin([FromBody] JellyfinSyncRequest req)
    {
        var plugin = _pm.Get("com.channelflow.jellyfin");
        if (plugin?.MediaSource is null) return BadRequest("jellyfin plugin not loaded");
        var conn = new SdkConnection(req.ConnectionId, "jellyfin", new Dictionary<string, string> { ["url"] = req.Url, ["apiKey"] = req.ApiKey ?? "" });
        var result = await plugin.MediaSource.TestConnectionAsync(conn, req.ApiKey ?? "");
        return Ok(result);
    }

    [HttpGet("libraries/{connectionId:int}")]
    public async Task<IActionResult> Libraries(int connectionId)
    {
        var connection = await _db.Connections.FindAsync(connectionId);
        if (connection is null) return NotFound("connection not found");

        Dictionary<string, string> config;
        try { config = JsonSerializer.Deserialize<Dictionary<string, string>>(connection.ConfigJson) ?? new(); }
        catch { config = new(); }

        var plugin = _pm.Get("com.channelflow.jellyfin");
        if (plugin?.MediaSource is null) return BadRequest("jellyfin plugin not loaded");

        var conn = new SdkConnection(connection.Id.ToString(), connection.Kind, config);
        var apiKey = config.TryGetValue("apiKey", out var ak) ? ak : "";
        try
        {
            var libs = await plugin.MediaSource.ListLibrariesAsync(conn, apiKey);
            return Ok(libs);
        }
        catch (Exception e)
        {
            return Ok(new List<object>()); // Return empty list instead of error
        }
    }

    [HttpPost("sync-jellyfin")]
    public async Task<IActionResult> SyncJellyfin([FromBody] JellyfinSyncRequest req)
    {
        var plugin = _pm.Get("com.channelflow.jellyfin");
        if (plugin?.MediaSource is null) return BadRequest("jellyfin plugin not loaded");
        var taskKey = $"jellyfin.sync.{req.LibraryId}";
        _tasks.Report(taskKey, "Jellyfin Sync", req.LibraryId, 0, 1, "running");
        var conn = new SdkConnection(req.ConnectionId, "jellyfin", new Dictionary<string, string> { ["url"] = req.Url, ["apiKey"] = req.ApiKey ?? "" });
        if (req.LibraryId is not null)
        {
            var ctx = new SyncContext(req.ConnectionId, req.LibraryId, _storage, _logger, _core, req.ApiKey ?? "", conn, "movie");
            var report = await plugin.MediaSource.SyncLibraryAsync(ctx);
            _tasks.Report(taskKey, "Jellyfin Sync", req.LibraryId, report.ItemsAdded, Math.Max(1, report.ItemsAdded), report.Errors.Count > 0 ? "failed" : "done", string.Join("; ", report.Errors));
            return Ok(report);
        }
        _tasks.Report(taskKey, "Jellyfin Sync", req.LibraryId, 0, 1, "failed", "libraryId required");
        return BadRequest("libraryId required");
    }

    [HttpPost("sync-libraries")]
    public async Task<IActionResult> SyncLibraries([FromBody] SyncLibrariesRequest req)
    {
        var connection = await _db.Connections.FindAsync(req.ConnectionId);
        if (connection is null) return NotFound("connection not found");

        Dictionary<string, string> config;
        try { config = JsonSerializer.Deserialize<Dictionary<string, string>>(connection.ConfigJson) ?? new(); }
        catch { config = new(); }

        var plugin = _pm.Get("com.channelflow.jellyfin");
        if (plugin?.MediaSource is null) return BadRequest("jellyfin plugin not loaded");

        var conn = new SdkConnection(connection.Id.ToString(), connection.Kind, config);
        var apiKey = config.TryGetValue("apiKey", out var ak) ? ak : "";
        var reports = new List<object>();
        foreach (var libId in req.LibraryIds)
        {
            var mediaType = req.MediaTypes?.TryGetValue(libId, out var mt) == true ? mt : "movie";
            var taskKey = $"jellyfin.sync.{connection.Id}.{libId}";
            _tasks.Report(taskKey, "Jellyfin Sync", $"{connection.Name ?? "Jellyfin"}: {libId}", 0, 1, "running");
            var ctx = new SyncContext(connection.Id.ToString(), libId, _storage, _logger, _core, apiKey, conn, mediaType);
            var report = await plugin.MediaSource.SyncLibraryAsync(ctx);
            _tasks.Report(taskKey, "Jellyfin Sync", $"{connection.Name ?? "Jellyfin"}: {libId}", report.ItemsAdded, Math.Max(1, report.ItemsAdded), report.Errors.Count > 0 ? "failed" : "done", string.Join("; ", report.Errors));
            reports.Add(new { libraryId = libId, mediaType, report });
        }
        return Ok(reports);
    }

    [HttpPost("sync-staged")]
    public async Task<IActionResult> SyncStaged([FromBody] SyncLibrariesRequest req)
    {
        var connection = await _db.Connections.FindAsync(req.ConnectionId);
        if (connection is null) return NotFound("connection not found");
        Dictionary<string, string> config;
        try { config = JsonSerializer.Deserialize<Dictionary<string, string>>(connection.ConfigJson) ?? new(); }
        catch { config = new(); }
        var plugin = _pm.Get("com.channelflow.jellyfin");
        if (plugin?.MediaSource is null) return BadRequest("jellyfin plugin not loaded");
        var conn = new SdkConnection(connection.Id.ToString(), connection.Kind, config);
        var apiKey = config.TryGetValue("apiKey", out var ak) ? ak : "";

        var taskKey = $"jellyfin.staged.{connection.Id}";
        _tasks.Report(taskKey, "Jellyfin Staged Sync", "Starting", 0, 6, "running");

        var reports = new List<object>();
        // Group libraries by media type order
        var typeOrder = new[] { "movie", "series", "album", "musicvideo" };
        var libsByType = new Dictionary<string, List<string>>();
        foreach (var libId in req.LibraryIds)
        {
            var mt = req.MediaTypes?.TryGetValue(libId, out var m) == true ? m : "movie";
            if (!libsByType.ContainsKey(mt)) libsByType[mt] = new();
            libsByType[mt].Add(libId);
        }

        int stage = 0;
        foreach (var mt in typeOrder)
        {
            if (!libsByType.ContainsKey(mt) || libsByType[mt].Count == 0) continue;
            stage++;
            var typeName = mt switch { "movie" => "Movies", "series" => "TV Shows", "album" => "Music", "musicvideo" => "Music Videos", _ => mt };
            _tasks.Report(taskKey, "Jellyfin Staged Sync", $"Stage {stage}: {typeName}", stage, 6, "running");
            foreach (var libId in libsByType[mt])
            {
                var ctx = new SyncContext(connection.Id.ToString(), libId, _storage, _logger, _core, apiKey, conn, mt, _tasks, taskKey);
                var report = await plugin.MediaSource.SyncLibraryAsync(ctx);
                reports.Add(new { libraryId = libId, mediaType = mt, report });
            }
        }

        // Stage: download posters
        stage++;
        _tasks.Report(taskKey, "Jellyfin Staged Sync", "Stage 5: Downloading posters", stage, 6, "running");
        try
        {
            var ctx = new SyncContext(connection.Id.ToString(), req.LibraryIds.FirstOrDefault() ?? "", _storage, _logger, _core, apiKey, conn, "movie", _tasks, taskKey);
            var pr = await plugin.MediaSource.DownloadPostersAsync(ctx);
            reports.Add(new { stage = "posters", report = pr });
        }
        catch (Exception e) { _tasks.Report(taskKey, "Jellyfin Staged Sync", "Poster download failed", stage, 6, "failed", e.Message); }

        // Stage: download people images
        stage++;
        _tasks.Report(taskKey, "Jellyfin Staged Sync", "Stage 6: Downloading people images", stage, 6, "running");
        try
        {
            var ctx = new SyncContext(connection.Id.ToString(), req.LibraryIds.FirstOrDefault() ?? "", _storage, _logger, _core, apiKey, conn, "movie", _tasks, taskKey);
            var pr = await plugin.MediaSource.DownloadPeopleImagesAsync(ctx);
            reports.Add(new { stage = "people", report = pr });
        }
        catch (Exception e) { _tasks.Report(taskKey, "Jellyfin Staged Sync", "People image download failed", stage, 6, "failed", e.Message); }

        _tasks.Report(taskKey, "Jellyfin Staged Sync", "Complete", 6, 6, "done");
        return Ok(reports);
    }

    [HttpGet("{*matchKey}")]
    public async Task<IActionResult> Get(string matchKey)
    {
        using var scope = HttpContext.RequestServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.MatchKey == matchKey);
        if (item is null) return NotFound();
        var credits = await _core.GetCreditsForMediaAsync(matchKey);
        var personIds = credits.Select(c => c.PersonId).Distinct().ToList();
        var people = await db.People.Where(p => personIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var enriched = credits.Select(c => new
        {
            c.Id,
            c.PersonId,
            personName = people.TryGetValue(c.PersonId, out var p) ? p.Name : "",
            imagePath = people.TryGetValue(c.PersonId, out var p2) ? p2.ImagePath : c.ImagePath,
            c.MediaMatchKey,
            c.Role,
            c.Character
        }).ToList();
        return Ok(new { item, credits = enriched });
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog([FromQuery] string? kind, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        var lim = limit ?? 100;
        var off = offset ?? 0;
        var filter = new CatalogFilter(
            Kinds: string.IsNullOrEmpty(kind) ? new List<string>() : new List<string> { kind },
            Genres: new List<string>(),
            Tags: new List<string>(),
            Limit: lim + off);
        var rows = await _core.CatalogAsync(filter);
        var total = rows.Count;
        if (off > 0) rows = rows.Skip(off).ToList();
        if (rows.Count > lim) rows = rows.Take(lim).ToList();
        return Ok(new { items = rows, count = total, offset = off, limit = lim });
    }
}
