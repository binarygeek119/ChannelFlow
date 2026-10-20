using ChannelFlow.Database;
using ChannelFlow.PluginSdk;
using SdkConnection = ChannelFlow.PluginSdk.Connection;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core;

public class PastTenseThumbnailService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<PastTenseThumbnailService> _logger;

    public PastTenseThumbnailService(IServiceProvider sp, ILogger<PastTenseThumbnailService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit on startup
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnce(stoppingToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Past Tense thumbnail extraction failed");
            }
            // Run daily
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
            var taskKey = $"pasttense.thumbs.{conn.Id}";
            tracker.Report(taskKey, "Past Tense Thumbnails", "Starting", 0, 1, "running");
            var ctx = new SyncContext(conn.Id.ToString(), path, new InMemoryPluginStorage(), new ConsolePluginLogger("pasttense"), core, apiKey, sdkConn, "news");
            var report = await plugin.MediaSource.ExtractThumbnailsAsync(ctx);
            tracker.Report(taskKey, "Past Tense Thumbnails", "Done", report.ItemsAdded, Math.Max(1, report.ItemsAdded), report.Errors.Count > 0 ? "failed" : "done", string.Join("; ", report.Errors));
        }
    }
}