using ChannelFlow.Database;
using ChannelFlow.PluginSdk;
using SdkConnection = ChannelFlow.PluginSdk.Connection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ChannelFlow.Core.Controllers;

public record JellyfinSyncRequest(string Url, string ApiKey, string? LibraryId = null, string ConnectionId = "jellyfin-1");
public record SyncLibrariesRequest(int ConnectionId, List<string> LibraryIds);

[ApiController]
[Route("api/media")]
public class MediaController : ControllerBase
{
    private readonly PluginManager _pm;
    private readonly ICoreData _core;
    private readonly IPluginStorage _storage;
    private readonly IPluginLogger _logger;
    private readonly ChannelFlowDbContext _db;
    public MediaController(PluginManager pm, ICoreData core, ChannelFlowDbContext db)
    {
        _pm = pm; _core = core; _db = db;
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
        var libs = await plugin.MediaSource.ListLibrariesAsync(conn, apiKey);
        return Ok(libs);
    }

    [HttpPost("sync-jellyfin")]
    public async Task<IActionResult> SyncJellyfin([FromBody] JellyfinSyncRequest req)
    {
        var plugin = _pm.Get("com.channelflow.jellyfin");
        if (plugin?.MediaSource is null) return BadRequest("jellyfin plugin not loaded");
        var conn = new SdkConnection(req.ConnectionId, "jellyfin", new Dictionary<string, string> { ["url"] = req.Url, ["apiKey"] = req.ApiKey ?? "" });
        if (req.LibraryId is not null)
        {
            var ctx = new SyncContext(req.ConnectionId, req.LibraryId, _storage, _logger, _core, req.ApiKey ?? "", conn);
            var report = await plugin.MediaSource.SyncLibraryAsync(ctx);
            return Ok(report);
        }
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
            var ctx = new SyncContext(connection.Id.ToString(), libId, _storage, _logger, _core, apiKey, conn);
            var report = await plugin.MediaSource.SyncLibraryAsync(ctx);
            reports.Add(new { libraryId = libId, report });
        }
        return Ok(reports);
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
