using System.Text.Json;
using System.Xml.Linq;
using ChannelFlow.PluginSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ChannelFlow.Plugin.News;

public class NewsSettings
{
    public string Header { get; set; } = "FlowWire News";
    public int ArticleCount { get; set; } = 5;
    public int RefreshMinutes { get; set; } = 15;
    public string TtsVoice { get; set; } = "alloy";
    public string TtsEngine { get; set; } = "openai";
    public string AnchorIntro { get; set; } = "";
    public string AnchorOutro { get; set; } = "";
    public bool BulletinEnabled { get; set; } = false;
    public List<string> BulletinMarks { get; set; } = new() { "08:00", "12:00", "17:00", "21:00" };
    public List<string> Feeds { get; set; } = new();
}

public class NewsPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.news", "News", "3.0.0", "3.0.0", Category: "news",
        Permissions: new List<string> { "storage:read", "storage:write", "network:outbound", "api:routes", "ui:pages" });

    private PluginApi? _api;
    private NewsSettings _settings = new();
    private readonly object _lock = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public Task OnLoadAsync(PluginApi api)
    {
        _api = api;
        _ = LoadSettingsAsync();
        return Task.CompletedTask;
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var val = await _api!.Storage.GetAsync("settings");
            if (val.HasValue)
            {
                var s = JsonSerializer.Deserialize<NewsSettings>(val.Value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (s is not null) { lock (_lock) { _settings = s; } }
            }
        }
        catch { }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            NewsSettings s;
            lock (_lock) { s = _settings; }
            var json = JsonSerializer.SerializeToElement(s);
            await _api!.Storage.SetAsync("settings", json);
        }
        catch { }
    }

    public Task OnEnableAsync() => Task.CompletedTask;
    public Task OnDisableAsync() => Task.CompletedTask;
    public void OnUnload() { }
    public Task OnConfigAsync(JsonElement config) => Task.CompletedTask;

    public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", () => { lock (_lock) { return Results.Ok(new { settings = _settings }); } });

        endpoints.MapPut("/", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                var s = JsonSerializer.Deserialize<NewsSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (s is null) return Results.BadRequest(new { error = "invalid body" });
                lock (_lock) { _settings = s; }
                await SaveSettingsAsync();
                return Results.Ok(new { settings = s });
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });

        // Headline preview: fetch from RSS feeds
        endpoints.MapGet("/headlines", async () =>
        {
            NewsSettings s;
            lock (_lock) { s = _settings; }
            var headlines = new List<object>();
            foreach (var feed in s.Feeds.Take(10))
            {
                try
                {
                    var xml = await _http.GetStringAsync(feed);
                    var doc = XDocument.Parse(xml);
                    foreach (var item in doc.Descendants("item").Take(s.ArticleCount))
                    {
                        var title = item.Element("title")?.Value ?? "";
                        var link = item.Element("link")?.Value ?? "";
                        var desc = item.Element("description")?.Value ?? "";
                        headlines.Add(new { title, link, description = desc, feed });
                    }
                }
                catch { }
            }
            return Results.Ok(new { headlines, count = headlines.Count });
        });

        // Test fetch from a single feed
        endpoints.MapPost("/fetch-feed", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var url = doc.RootElement.GetProperty("url").GetString() ?? "";
                if (string.IsNullOrEmpty(url)) return Results.BadRequest(new { error = "url required" });
                var xml = await _http.GetStringAsync(url);
                var xdoc = XDocument.Parse(xml);
                var items = xdoc.Descendants("item").Take(10).Select(i => new
                {
                    title = i.Element("title")?.Value ?? "",
                    link = i.Element("link")?.Value ?? "",
                    description = i.Element("description")?.Value ?? ""
                }).ToList();
                return Results.Ok(new { ok = true, count = items.Count, items });
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });
    }

    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => null;
}
