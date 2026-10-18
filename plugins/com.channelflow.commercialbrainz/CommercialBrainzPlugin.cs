using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ChannelFlow.PluginSdk;

namespace ChannelFlow.Plugin.CommercialBrainz;

public class CbSettings
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://commercialbrainz.org";
    public string ApiToken { get; set; } = "";
    public int MaxSyncResults { get; set; } = 500;
    public int? MinYear { get; set; }
    public int? MaxYear { get; set; }
    public List<int> Decades { get; set; } = new();
    public List<string> Brands { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public List<string> ExcludeTags { get; set; } = new();
    public List<string> Genres { get; set; } = new();
    public List<string> Networks { get; set; } = new();
    public List<string> ChannelNames { get; set; } = new();
    public bool AllowSpoof { get; set; } = true;
    public bool AllowFake { get; set; } = true;
    public bool AllowReal { get; set; } = true;
    public bool AllowAiEnhanced { get; set; } = true;
    public bool AllowLateNight { get; set; } = true;
    public bool AllowAdultRated { get; set; } = false;
    public bool AllowBanned { get; set; } = false;
}

public class CommercialBrainzPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.commercialbrainz", "CommercialBrainz", "3.0.0", "3.0.0", Category: "commercials",
        Permissions: new List<string> { "storage:read", "storage:write", "network:outbound", "api:routes", "ui:pages" });

    private PluginApi? _api;
    private CbSettings _settings = new();
    private readonly object _lock = new();

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
                var s = JsonSerializer.Deserialize<CbSettings>(val.Value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (s is not null) { lock (_lock) { _settings = s; } }
            }
        }
        catch { }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            CbSettings s;
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
                var s = JsonSerializer.Deserialize<CbSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (s is null) return Results.BadRequest(new { error = "invalid body" });
                // normalize
                s.BaseUrl = string.IsNullOrWhiteSpace(s.BaseUrl) ? "https://commercialbrainz.org" : s.BaseUrl.Trim().TrimEnd('/');
                s.MaxSyncResults = Math.Clamp(s.MaxSyncResults, 1, 500);
                lock (_lock) { _settings = s; }
                await SaveSettingsAsync();
                return Results.Ok(new { settings = s });
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });
        endpoints.MapGet("/test", async () =>
        {
            CbSettings s;
            lock (_lock) { s = _settings; }
            if (!s.Enabled) return Results.Ok(new { result = new { ok = false, code = "disabled", detail = "CommercialBrainz is disabled." } });
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.Add("User-Agent", "ChannelFlow/3.0.0 (https://github.com/binarygeek119/ChannelFlow)");
                var url = $"{s.BaseUrl}/api/v1/browse/videos?limit=1";
                var req = http.GetAsync(url);
                if (!string.IsNullOrWhiteSpace(s.ApiToken)) req = http.GetAsync(url); // token added below
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(s.ApiToken))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", s.ApiToken.Trim());
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                var res = await http.SendAsync(request);
                if (res.IsSuccessStatusCode)
                    return Results.Ok(new { result = new { ok = true, code = "ok", detail = "Reachable, token accepted" } });
                if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized || res.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var text = await res.Content.ReadAsStringAsync();
                    return Results.Ok(new { result = new { ok = false, code = "auth_failed", detail = text } });
                }
                return Results.Ok(new { result = new { ok = false, code = "bad_url", detail = $"Unexpected response ({(int)res.StatusCode})" } });
            }
            catch (Exception e) { return Results.Ok(new { result = new { ok = false, code = "unreachable", detail = "Could not reach the server: " + e.Message } }); }
        });
    }

    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => null;
}
