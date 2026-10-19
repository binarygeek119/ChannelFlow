using ChannelFlow.PluginSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

public record JellyfinSyncRequest(string Url, string ApiKey, string? LibraryId = null, string ConnectionId = "jellyfin-1");

[ApiController]
[Route("api/media")]
public class MediaController : ControllerBase
{
    private readonly PluginManager _pm;
    private readonly ICoreData _core;
    private readonly IPluginStorage _storage;
    private readonly IPluginLogger _logger;
    public MediaController(PluginManager pm, ICoreData core)
    {
        _pm = pm; _core = core;
        _storage = new InMemoryPluginStorage();
        _logger = new ConsolePluginLogger("media");
    }

    [HttpPost("sync-jellyfin")]
    public async Task<IActionResult> SyncJellyfin([FromBody] JellyfinSyncRequest req)
    {
        var plugin = _pm.Get("com.channelflow.jellyfin");
        if (plugin?.MediaSource is null) return BadRequest("jellyfin plugin not loaded");
        var conn = new Connection(req.ConnectionId, "jellyfin", new Dictionary<string, string> { ["url"] = req.Url, ["apiKey"] = req.ApiKey });
        var ctx = new SyncContext(req.ConnectionId, req.LibraryId, _storage, _logger, _core, req.ApiKey, conn);
        var report = await plugin.MediaSource.SyncLibraryAsync(ctx);
        return Ok(report);
    }

    [HttpGet("{*matchKey}")]
    public async Task<IActionResult> Get(string matchKey)
    {
        using var scope = HttpContext.RequestServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlow.Database.ChannelFlowDbContext>();
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.MatchKey == matchKey);
        if (item is null) return NotFound();
        var credits = await _core.GetCreditsForMediaAsync(matchKey);
        return Ok(new { item, credits });
    }

    [HttpPost("test-jellyfin")]
    public async Task<IActionResult> TestJellyfin([FromBody] JellyfinSyncRequest req)
    {
        var plugin = _pm.Get("com.channelflow.jellyfin");
        if (plugin?.MediaSource is null) return BadRequest("jellyfin plugin not loaded");
        var conn = new Connection(req.ConnectionId, "jellyfin", new Dictionary<string, string> { ["url"] = req.Url, ["apiKey"] = req.ApiKey });
        var result = await plugin.MediaSource.TestConnectionAsync(conn, req.ApiKey);
        return Ok(result);
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
            Limit: lim + off); // fetch enough for offset
        var rows = await _core.CatalogAsync(filter);
        var total = rows.Count;
        if (off > 0) rows = rows.Skip(off).ToList();
        if (rows.Count > lim) rows = rows.Take(lim).ToList();
        return Ok(new { items = rows, count = total, offset = off, limit = lim });
    }
}
