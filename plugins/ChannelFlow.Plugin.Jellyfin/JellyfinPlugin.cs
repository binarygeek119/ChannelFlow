using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ChannelFlow.PluginSdk;

namespace ChannelFlow.Plugin.Jellyfin;

public class JellyfinMediaSource : IMediaSource
{
    public string TypeId => "jellyfin";
    public string DisplayName => "Jellyfin";
    public List<FieldSpec> ConnectionFields => new()
    {
        FieldSpec.Text("url", "Server URL", true),
        FieldSpec.Text("apiKey", "API Key", true)
    };
    public List<MediaType> SupportedMedia => new() { MediaType.Movie, MediaType.Series };

    private static HttpClient Client(string apiKey)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
        };
        var c = new HttpClient(handler);
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("MediaBrowser", $"Token=\"{apiKey}\"");
        c.Timeout = TimeSpan.FromMinutes(10);
        return c;
    }

    public async Task<TestResult> TestConnectionAsync(Connection conn, string apiKey)
    {
        try
        {
            var url = conn.Config["url"].TrimEnd('/');
            using var c = Client(apiKey);
            Console.WriteLine($"[jellyfin] headers: {string.Join(", ", c.DefaultRequestHeaders.Select(h => h.Key + "=" + string.Join(" ", h.Value)))}");
            var res = await c.GetAsync(url + "/System/Info");
            Console.WriteLine($"[jellyfin] response: {(int)res.StatusCode}");
            if (!res.IsSuccessStatusCode) return new TestResult(false, $"HTTP {(int)res.StatusCode}");
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var name = doc.RootElement.TryGetProperty("ServerName", out var sn) ? sn.GetString() : "Jellyfin";
            return new TestResult(true, name ?? "ok");
        }
        catch (Exception e) { return new TestResult(false, e.Message); }
    }

    public async Task<List<Library>> ListLibrariesAsync(Connection conn, string apiKey)
    {
        var url = conn.Config["url"].TrimEnd('/');
        using var c = Client(apiKey);
        var res = await c.GetAsync(url + "/Library/MediaFolders");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var libs = new List<Library>();
        if (doc.RootElement.TryGetProperty("Items", out var items))
        {
            foreach (var it in items.EnumerateArray())
            {
                var id = it.TryGetProperty("Id", out var idProp) ? idProp.GetString() ?? "" : (it.TryGetProperty("id", out var idProp2) ? idProp2.GetString() ?? "" : "");
                var name = it.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? id : (it.TryGetProperty("name", out var nameProp2) ? nameProp2.GetString() ?? id : id);
                var type = it.TryGetProperty("CollectionType", out var ct) ? ct.GetString() : null;
                var mt = type?.ToLowerInvariant() switch
                {
                    "movies" => MediaType.Movie,
                    "tvshows" => MediaType.Series,
                    _ => MediaType.Movie
                };
                libs.Add(new Library(id, name, mt));
            }
        }
        return libs;
    }

    public async Task<SyncReport> SyncLibraryAsync(SyncContext ctx)
    {
        var conn = ctx.Connection;
        if (conn is null) return new SyncReport(0, 0, 0, new List<string> { "No connection provided" });
        var url = conn.Config.TryGetValue("url", out var u) ? u.TrimEnd('/') : "";
        var apiKey = ctx.ApiKey;
        using var c = Client(apiKey);
        int added = 0;
        var errors = new List<string>();
        try
        {
            var fetchUrl = $"{url}/Items?ParentId={ctx.LibraryId}&Recursive=true&IncludeItemTypes=Movie,Series,Episode&Fields=Genres,OfficialRating,RunTimeTicks,ProductionYear&Limit=10000";
            var res = await c.GetAsync(fetchUrl);
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var items = new List<MediaItemDto>();
            if (doc.RootElement.TryGetProperty("Items", out var arr))
            {
                foreach (var it in arr.EnumerateArray())
                {
                    var id = it.TryGetProperty("Id", out var idProp) ? idProp.GetString() ?? "" : (it.TryGetProperty("id", out var idProp2) ? idProp2.GetString() ?? "" : "");
                    var name = it.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? id : (it.TryGetProperty("name", out var nameProp2) ? nameProp2.GetString() ?? id : id);
                    var type = it.TryGetProperty("Type", out var t) ? t.GetString() : "Movie";
                    var kind = ctx.MediaType != "movie" ? ctx.MediaType : (type?.ToLowerInvariant() switch
                    {
                        "series" => "series",
                        "episode" => "episode",
                        _ => "movie"
                    });
                    int? year = it.TryGetProperty("ProductionYear", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;
                    long? runtime = null;
                    if (it.TryGetProperty("RunTimeTicks", out var rt) && rt.ValueKind == JsonValueKind.Number)
                        runtime = rt.GetInt64() / 10000;
                    var genres = new List<string>();
                    if (it.TryGetProperty("Genres", out var g) && g.ValueKind == JsonValueKind.Array)
                        foreach (var gg in g.EnumerateArray()) if (gg.GetString() is string gs) genres.Add(gs);
                    string? rating = it.TryGetProperty("OfficialRating", out var r) ? r.GetString() : null;
                    var posterUrl = $"{url}/Items/{id}/Images/Primary?maxHeight=300&quality=90";
                    var webUrl = $"{url}/web/index.html#!/details?id={id}";
                    items.Add(new MediaItemDto(
                        MatchKey: $"jellyfin:{id}",
                        Kind: kind,
                        Title: name,
                        Year: year,
                        RuntimeMs: runtime,
                        Genres: genres,
                        OfficialRating: rating,
                        Tags: new List<string>(),
                        ConnectionId: ctx.ConnectionId,
                        LibraryId: ctx.LibraryId,
                        PosterUrl: posterUrl,
                        WebUrl: webUrl,
                        SourceKind: "jellyfin"));
                }
            }
            await ctx.Core.UpsertMediaItemsAsync(items);
            added = items.Count;
        }
        catch (Exception e) { errors.Add(e.Message); }
        return new SyncReport(added, 0, 0, errors);
    }
}

public class JellyfinPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.jellyfin", "Jellyfin", "3.0.0", "3.0.0", Category: "media-source");
    private PluginApi? _api;
    private JellyfinMediaSource? _ms;
    public Task OnLoadAsync(PluginApi api) { _api = api; _ms = new JellyfinMediaSource(); return Task.CompletedTask; }
    public Task OnEnableAsync() => Task.CompletedTask;
    public Task OnDisableAsync() => Task.CompletedTask;
    public void OnUnload() { }
    public Task OnConfigAsync(JsonElement config) => Task.CompletedTask;
    public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) { }
    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => _ms;
}
