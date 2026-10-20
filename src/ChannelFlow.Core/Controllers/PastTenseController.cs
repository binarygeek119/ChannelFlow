using ChannelFlow.Database;
using ChannelFlow.PluginSdk;
using SdkConnection = ChannelFlow.PluginSdk.Connection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/pasttense")]
public class PastTenseController : ControllerBase
{
    private readonly PluginManager _pm;
    private readonly ICoreData _core;
    private readonly IPluginStorage _storage;
    private readonly IPluginLogger _logger;
    private readonly ChannelFlowDbContext _db;
    private readonly TaskTracker _tasks;
    public PastTenseController(PluginManager pm, ICoreData core, ChannelFlowDbContext db, TaskTracker tasks)
    {
        _pm = pm; _core = core; _db = db; _tasks = tasks;
        _storage = new InMemoryPluginStorage();
        _logger = new ConsolePluginLogger("pasttense");
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] Dictionary<string, string> config)
    {
        var plugin = _pm.Get("com.channelflow.pasttense");
        if (plugin?.MediaSource is null) return BadRequest("plugin not loaded");
        var conn = new SdkConnection("pt-1", "pasttense", config);
        var apiKey = config.TryGetValue("apiKey", out var ak) ? ak : "";
        var result = await plugin.MediaSource.TestConnectionAsync(conn, apiKey);
        return Ok(result);
    }

    [HttpGet("libraries")]
    public async Task<IActionResult> Libraries([FromQuery] int connectionId)
    {
        var connection = await _db.Connections.FindAsync(connectionId);
        if (connection is null) return NotFound();
        Dictionary<string, string> config;
        try { config = JsonSerializer.Deserialize<Dictionary<string, string>>(connection.ConfigJson) ?? new(); }
        catch { config = new(); }
        var plugin = _pm.Get("com.channelflow.pasttense");
        if (plugin?.MediaSource is null) return BadRequest("plugin not loaded");
        var conn = new SdkConnection(connection.Id.ToString(), connection.Kind, config);
        var apiKey = config.TryGetValue("apiKey", out var ak) ? ak : "";
        var libs = await plugin.MediaSource.ListLibrariesAsync(conn, apiKey);
        return Ok(libs);
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] PastTenseSyncRequest req)
    {
        var connection = await _db.Connections.FindAsync(req.ConnectionId);
        if (connection is null) return NotFound();
        Dictionary<string, string> config;
        try { config = JsonSerializer.Deserialize<Dictionary<string, string>>(connection.ConfigJson) ?? new(); }
        catch { config = new(); }
        var plugin = _pm.Get("com.channelflow.pasttense");
        if (plugin?.MediaSource is null) return BadRequest("plugin not loaded");
        var conn = new SdkConnection(connection.Id.ToString(), connection.Kind, config);
        var apiKey = config.TryGetValue("apiKey", out var ak) ? ak : "";
        var path = config.TryGetValue("path", out var p) ? p : "";
        var taskKey = $"pasttense.sync.{connection.Id}";
        _tasks.Report(taskKey, "Past Tense Sync", "Starting", 0, 1, "running");
        var ctx = new SyncContext(connection.Id.ToString(), path, _storage, _logger, _core, apiKey, conn, "news");
        var report = await plugin.MediaSource.SyncLibraryAsync(ctx);
        _tasks.Report(taskKey, "Past Tense Sync", "Done", report.ItemsAdded, Math.Max(1, report.ItemsAdded), report.Errors.Count > 0 ? "failed" : "done", string.Join("; ", report.Errors));
        return Ok(report);
    }

    [HttpPost("thumbnails")]
    public async Task<IActionResult> Thumbnails([FromBody] PastTenseSyncRequest req)
    {
        var connection = await _db.Connections.FindAsync(req.ConnectionId);
        if (connection is null) return NotFound();
        Dictionary<string, string> config;
        try { config = JsonSerializer.Deserialize<Dictionary<string, string>>(connection.ConfigJson) ?? new(); }
        catch { config = new(); }
        var plugin = _pm.Get("com.channelflow.pasttense");
        if (plugin?.MediaSource is null) return BadRequest("plugin not loaded");
        var conn = new SdkConnection(connection.Id.ToString(), connection.Kind, config);
        var apiKey = config.TryGetValue("apiKey", out var ak) ? ak : "";
        var path = config.TryGetValue("path", out var p) ? p : "";
        var taskKey = $"pasttense.thumbs.{connection.Id}";
        _tasks.Report(taskKey, "Past Tense Thumbnails", "Starting", 0, 1, "running");
        var ctx = new SyncContext(connection.Id.ToString(), path, _storage, _logger, _core, apiKey, conn, "news");
        var report = await plugin.MediaSource.ExtractThumbnailsAsync(ctx);
        _tasks.Report(taskKey, "Past Tense Thumbnails", "Done", report.ItemsAdded, Math.Max(1, report.ItemsAdded), report.Errors.Count > 0 ? "failed" : "done", string.Join("; ", report.Errors));
        return Ok(report);
    }
}

public record PastTenseSyncRequest(int ConnectionId, string? LibraryId = null);