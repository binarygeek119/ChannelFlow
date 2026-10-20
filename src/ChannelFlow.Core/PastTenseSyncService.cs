using ChannelFlow.Database;
using ChannelFlow.PluginSdk;
using SdkConnection = ChannelFlow.PluginSdk.Connection;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core;

public class PastTenseSyncService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<PastTenseSyncService> _logger;

    public PastTenseSyncService(IServiceProvider sp, ILogger<PastTenseSyncService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnce(stoppingToken); }
            catch (Exception e) { _logger.LogError(e, "Past Tense sync failed"); }
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var pm = scope.ServiceProvider.GetRequiredService<PluginManager>();
        var core = scope.ServiceProvider.GetRequiredService<ICoreData>();
        var tracker = scope.ServiceProvider.GetRequiredService<TaskTracker>();
        var plugin = pm.Get("com.channelflow.pasttense");
        if (plugin?.MediaSource is null) return;
        var connections = await db.Connections.AsNoTracking().Where(c => c.Kind == "pasttense").ToListAsync();
        foreach (var conn in connections)
        {
            Dictionary<string, string> config;
            try { config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(conn.ConfigJson) ?? new(); }
            catch { config = new(); }
            var sdkConn = new SdkConnection(conn.Id.ToString(), conn.Kind, config);
            var apiKey = config.TryGetValue("apiKey", out var ak) ? ak : "";
            var path = config.TryGetValue("path", out var p) ? p : "";
            var taskKey = $"pasttense.sync.{conn.Id}";
            tracker.Report(taskKey, "Past Tense Sync", "Starting", 0, 1, "running");
            var ctx = new SyncContext(conn.Id.ToString(), path, new InMemoryPluginStorage(), new ConsolePluginLogger("pasttense"), core, apiKey, sdkConn, "news");
            var report = await plugin.MediaSource.SyncLibraryAsync(ctx);
            tracker.Report(taskKey, "Past Tense Sync", "Done", report.ItemsAdded, Math.Max(1, report.ItemsAdded), report.Errors.Count > 0 ? "failed" : "done", string.Join("; ", report.Errors));
            // Also extract thumbnails
            var thumbKey = $"pasttense.thumbs.{conn.Id}";
            tracker.Report(thumbKey, "Past Tense Thumbnails", "Starting", 0, 1, "running");
            var thumbReport = await plugin.MediaSource.ExtractThumbnailsAsync(ctx);
            tracker.Report(thumbKey, "Past Tense Thumbnails", "Done", thumbReport.ItemsAdded, Math.Max(1, thumbReport.ItemsAdded), thumbReport.Errors.Count > 0 ? "failed" : "done", string.Join("; ", thumbReport.Errors));
        }
    }
}