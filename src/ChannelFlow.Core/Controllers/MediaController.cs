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
        if (item is null)
        {
            // Fallback: fetch from Jellyfin if not in catalog
            if (matchKey.StartsWith("jellyfin:"))
            {
                var jid = matchKey.Substring("jellyfin:".Length);
                // Find a jellyfin connection
                var conn = await db.Connections.FirstOrDefaultAsync(c => c.Kind == "jellyfin");
                if (conn is not null)
                {
                    Dictionary<string, string> config;
                    try { config = JsonSerializer.Deserialize<Dictionary<string, string>>(conn.ConfigJson) ?? new(); }
                    catch { config = new(); }
                    var url = config.TryGetValue("url", out var u) ? u.TrimEnd('/') : "";
                    var apiKey = config.TryGetValue("apiKey", out var ak) ? ak : "";
                    if (!string.IsNullOrEmpty(url))
                    {
                        try
                        {
                            using var http = new HttpClient();
                            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("MediaBrowser", $"Token=\"{apiKey}\"");
                            // Resolve userId
                            string? userId = null;
                            var usersRes = await http.GetAsync($"{url}/Users");
                            if (usersRes.IsSuccessStatusCode)
                            {
                                var ujson = await usersRes.Content.ReadAsStringAsync();
                                using var udoc = System.Text.Json.JsonDocument.Parse(ujson);
                                if (udoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && udoc.RootElement.GetArrayLength() > 0)
                                    userId = udoc.RootElement[0].TryGetProperty("Id", out var uid) ? uid.GetString() : null;
                            }
                            if (!string.IsNullOrEmpty(userId))
                            {
                                var r = await http.GetAsync($"{url}/Users/{userId}/Items/{jid}?Fields=People,Genres,Overview,OfficialRating,RunTimeTicks,ProductionYear,ImageTags");
                                if (r.IsSuccessStatusCode)
                                {
                                    var json = await r.Content.ReadAsStringAsync();
                                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                                    var name = doc.RootElement.TryGetProperty("Name", out var n) ? n.GetString() : jid;
                                    int? year = doc.RootElement.TryGetProperty("ProductionYear", out var py) && py.ValueKind == System.Text.Json.JsonValueKind.Number ? py.GetInt32() : null;
                                    string? overview = doc.RootElement.TryGetProperty("Overview", out var ov) ? ov.GetString() : null;
                                    List<string> genres = new();
                                    if (doc.RootElement.TryGetProperty("Genres", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.Array)
                                        foreach (var gg in g.EnumerateArray()) if (gg.GetString() is string gs) genres.Add(gs);
                                    string? rating = doc.RootElement.TryGetProperty("OfficialRating", out var rr) ? rr.GetString() : null;
                                    long? runtime = null;
                                    if (doc.RootElement.TryGetProperty("RunTimeTicks", out var rt) && rt.ValueKind == System.Text.Json.JsonValueKind.Number) runtime = rt.GetInt64() / 10000;
                                    string? posterUrl = null;
                                    if (doc.RootElement.TryGetProperty("ImageTags", out var it) && it.ValueKind == System.Text.Json.JsonValueKind.Object && it.TryGetProperty("Primary", out _))
                                        posterUrl = $"{url}/Items/{jid}/Images/Primary?maxHeight=300&quality=90";
                                    string? webUrl = $"{url}/web/index.html#!/details?id={jid}";
                                    // Build credits from Jellyfin People
                                    var fallbackCredits = new List<object>();
                                    if (doc.RootElement.TryGetProperty("People", out var peopleArr) && peopleArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    {
                                        foreach (var person in peopleArr.EnumerateArray())
                                        {
                                            var personName = person.TryGetProperty("Name", out var pn) ? pn.GetString() : null;
                                            if (string.IsNullOrEmpty(personName)) continue;
                                            var role = person.TryGetProperty("Type", out var pt) ? pt.GetString() ?? "Actor" : "Actor";
                                            var character = person.TryGetProperty("Role", out var pr) ? pr.GetString() : null;
                                            var personId = person.TryGetProperty("Id", out var pid) ? pid.GetString() : null;
                                            string? imagePath = null;
                                            if (!string.IsNullOrEmpty(personId) && person.TryGetProperty("PrimaryImageTag", out var pit) && pit.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(pit.GetString()))
                                                imagePath = $"{url}/Items/{personId}/Images/Primary?maxHeight=200&quality=90";
                                            fallbackCredits.Add(new { Id = Guid.Empty, PersonId = Guid.Empty, personName, imagePath, MediaMatchKey = matchKey, Role = role, Character = character });
                                        }
                                    }
                                    var fallbackItem = new MediaItem
                                    {
                                        MatchKey = matchKey,
                                        Kind = "movie",
                                        Title = name ?? jid,
                                        Year = year,
                                        RuntimeMs = runtime,
                                        Genres = genres,
                                        OfficialRating = rating,
                                        Tags = new(),
                                        PosterUrl = posterUrl,
                                        WebUrl = webUrl,
                                        SourceKind = "jellyfin",
                                        Overview = overview
                                    };
                                    return Ok(new { item = fallbackItem, credits = fallbackCredits });
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            return NotFound();
        }
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
        // Total count of matching items
        using var scope = HttpContext.RequestServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var totalQ = db.MediaItems.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(kind)) totalQ = totalQ.Where(m => m.Kind.ToLower() == kind.ToLower());
        var total = await totalQ.CountAsync();
        var filter = new CatalogFilter(
            Kinds: string.IsNullOrEmpty(kind) ? new List<string>() : new List<string> { kind },
            Genres: new List<string>(),
            Tags: new List<string>(),
            Limit: lim + off);
        var rows = await _core.CatalogAsync(filter);
        if (off > 0) rows = rows.Skip(off).ToList();
        if (rows.Count > lim) rows = rows.Take(lim).ToList();
        return Ok(new { items = rows, count = total, offset = off, limit = lim });
    }

    [HttpGet("collection/{*matchKey}")]
    public async Task<IActionResult> CollectionChildren(string matchKey)
    {
        // Look up the item in DB
        using var scope = HttpContext.RequestServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.MatchKey == matchKey);
        if (item is null) return NotFound();
        // If we have stored collection items, fetch their details from DB
        if (item.CollectionItems is { Count: > 0 })
        {
            string? jfUrl = null, jfKey = null, jfUserId = null;
            if (!string.IsNullOrEmpty(item.ConnectionId) && int.TryParse(item.ConnectionId, out var connId))
            {
                var conn = await db.Connections.FindAsync(connId);
                if (conn is not null)
                {
                    try
                    {
                        var cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(conn.ConfigJson);
                        if (cfg is not null) { cfg.TryGetValue("url", out jfUrl); cfg.TryGetValue("apiKey", out jfKey); }
                    }
                    catch { }
                    // Resolve userId
                    if (!string.IsNullOrEmpty(jfUrl) && !string.IsNullOrEmpty(jfKey))
                    {
                        try
                        {
                            using var http = new HttpClient();
                            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("MediaBrowser", $"Token=\"{jfKey}\"");
                            var r = await http.GetAsync($"{jfUrl.TrimEnd('/')}/Users");
                            if (r.IsSuccessStatusCode)
                            {
                                var json = await r.Content.ReadAsStringAsync();
                                using var doc = System.Text.Json.JsonDocument.Parse(json);
                                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                                    jfUserId = doc.RootElement[0].TryGetProperty("Id", out var uid) ? uid.GetString() : null;
                            }
                        }
                        catch { }
                    }
                }
            }
            var children = new List<object>();
            foreach (var cmk in item.CollectionItems)
            {
                var child = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.MatchKey == cmk);
                if (child is not null)
                {
                    children.Add(new { matchKey = child.MatchKey, title = child.Title, year = child.Year, posterUrl = child.PosterUrl });
                }
                else if (!string.IsNullOrEmpty(jfUrl) && !string.IsNullOrEmpty(jfKey) && !string.IsNullOrEmpty(jfUserId) && cmk.StartsWith("jellyfin:"))
                {
                    var jid = cmk.Substring("jellyfin:".Length);
                    string title = cmk;
                    int? year = null;
                    string? posterUrl = null;
                    try
                    {
                        using var http = new HttpClient();
                        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("MediaBrowser", $"Token=\"{jfKey}\"");
                        var r = await http.GetAsync($"{jfUrl.TrimEnd('/')}/Users/{jfUserId}/Items/{jid}");
                        if (r.IsSuccessStatusCode)
                        {
                            var json = await r.Content.ReadAsStringAsync();
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            title = doc.RootElement.TryGetProperty("Name", out var n) ? n.GetString() ?? cmk : cmk;
                            if (doc.RootElement.TryGetProperty("ProductionYear", out var py) && py.ValueKind == System.Text.Json.JsonValueKind.Number) year = py.GetInt32();
                            if (doc.RootElement.TryGetProperty("ImageTags", out var it) && it.ValueKind == System.Text.Json.JsonValueKind.Object && it.TryGetProperty("Primary", out _))
                                posterUrl = $"{jfUrl.TrimEnd('/')}/Items/{jid}/Images/Primary?maxHeight=300&quality=90";
                        }
                    }
                    catch { }
                    children.Add(new { matchKey = cmk, title, year, posterUrl });
                }
                else
                {
                    children.Add(new { matchKey = cmk, title = cmk, year = (int?)null, posterUrl = (string?)null });
                }
            }
            return Ok(new { children });
        }
        return Ok(new { children = Array.Empty<object>() });
    }

    [HttpGet("episodes/{*seriesMatchKey}")]
    public async Task<IActionResult> EpisodesForSeries(string seriesMatchKey)
    {
        using var scope = HttpContext.RequestServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var seriesId = seriesMatchKey.StartsWith("jellyfin:") ? seriesMatchKey.Substring("jellyfin:".Length) : seriesMatchKey;
        var eps = await db.MediaItems.AsNoTracking()
            .Where(m => m.Kind == "episode" && m.SeriesId == seriesId)
            .ToListAsync();
        var result = eps.Select(e => new
        {
            e.MatchKey,
            e.Title,
            e.Year,
            e.SeasonId,
            e.SeasonName,
            e.PosterUrl,
            e.WebUrl
        });
        return Ok(new { items = result, count = eps.Count });
    }
}
