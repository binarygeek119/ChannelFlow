using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChannelFlow.PluginSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ChannelFlow.Plugin.Weather;

public enum StarVariant { Ws4kp, Ws3kp }
public enum WeatherSource { Auto, Us, World }
public enum Units { Us, Si }

public class WeatherSettings
{
    public StarVariant WeatherstarVariant { get; set; } = StarVariant.Ws4kp;
    public WeatherSource Source { get; set; } = WeatherSource.Auto;
    public string DefaultLocation { get; set; } = "";
    public Units Units { get; set; } = Units.Us;
    public bool AutoWide169 { get; set; } = true;
    public List<string> Screens { get; set; } = new() { "hazards", "current", "latest_observations", "hourly", "hourly_graph", "travel", "regional", "local", "extended", "almanac", "spc_outlook", "radar" };
    public bool GuideEnabled { get; set; } = true;
    public string GuideBuildTime { get; set; } = "00:05";
}

public static class WeatherScreens
{
    public static readonly string[] All = { "hazards", "current", "latest_observations", "hourly", "hourly_graph", "travel", "regional", "local", "extended", "almanac", "spc_outlook", "radar" };
}

public class WeatherPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.weather", "Weather", "3.0.0", "3.0.0", Category: "weather",
        Permissions: new List<string> { "storage:read", "storage:write", "network:outbound", "api:routes", "ui:pages" });

    private PluginApi? _api;
    private WeatherSettings _settings = new();
    private readonly object _lock = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public Task OnLoadAsync(PluginApi api) { _api = api; _ = LoadSettingsAsync(); return Task.CompletedTask; }
    public Task OnEnableAsync() => Task.CompletedTask;
    public Task OnDisableAsync() => Task.CompletedTask;
    public void OnUnload() { }
    public Task OnConfigAsync(JsonElement config) => Task.CompletedTask;

    private async Task LoadSettingsAsync()
    {
        try
        {
            var val = await _api!.Storage.GetAsync("settings");
            if (val.HasValue)
            {
                var s = JsonSerializer.Deserialize<WeatherSettings>(val.Value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } });
                if (s is not null) { s.Screens = s.Screens.Where(sc => WeatherScreens.All.Contains(sc)).ToList(); if (string.IsNullOrWhiteSpace(s.GuideBuildTime)) s.GuideBuildTime = "00:05"; lock (_lock) { _settings = s; } }
            }
        }
        catch { }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            WeatherSettings s;
            lock (_lock) { s = _settings; }
            var json = JsonSerializer.SerializeToElement(s, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
            await _api!.Storage.SetAsync("settings", json);
        }
        catch { }
    }

    public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", () => { lock (_lock) { return Results.Ok(new { settings = _settings, screens = WeatherScreens.All }); } });

        endpoints.MapPut("/", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                var s = JsonSerializer.Deserialize<WeatherSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } });
                if (s is null) return Results.BadRequest(new { error = "invalid body" });
                s.Screens = s.Screens.Where(sc => WeatherScreens.All.Contains(sc)).ToList();
                s.DefaultLocation = s.DefaultLocation.Trim();
                if (string.IsNullOrWhiteSpace(s.GuideBuildTime)) s.GuideBuildTime = "00:05";
                lock (_lock) { _settings = s; }
                await SaveSettingsAsync();
                return Results.Ok(new { settings = s });
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });

        endpoints.MapGet("/forecast", async (string? location) =>
        {
            WeatherSettings s;
            lock (_lock) { s = _settings; }
            var loc = string.IsNullOrWhiteSpace(location) ? s.DefaultLocation : location;
            if (string.IsNullOrWhiteSpace(loc)) return Results.BadRequest(new { error = "location required" });
            try
            {
                double lat = 0, lon = 0;
                var parts = loc.Split(',');
                if (parts.Length == 2 && double.TryParse(parts[0].Trim(), out lat) && double.TryParse(parts[1].Trim(), out lon)) { }
                else
                {
                    using var http = new HttpClient();
                    var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(loc)}&count=1";
                    var geoRes = await http.GetFromJsonAsync<JsonElement>(geoUrl);
                    if (geoRes.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
                    {
                        var r = results[0];
                        lat = r.TryGetProperty("latitude", out var la) ? la.GetDouble() : 0;
                        lon = r.TryGetProperty("longitude", out var lo) ? lo.GetDouble() : 0;
                    }
                    else return Results.BadRequest(new { error = $"could not find {loc}" });
                }
                var units = s.Units == Units.Us ? "fahrenheit" : "celsius";
                var windUnits = s.Units == Units.Us ? "mph" : "kmh";
                var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,wind_speed_10m,relative_humidity_2m&hourly=temperature_2m,weather_code,precipitation_probability&daily=temperature_2m_max,temperature_2m_min,weather_code&temperature_unit={units}&wind_speed_unit={windUnits}&timezone=auto";
                using var http2 = new HttpClient();
                var forecast = await http2.GetFromJsonAsync<JsonElement>(url);
                return Results.Ok(new { location = loc, latitude = lat, longitude = lon, forecast });
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });
    }

    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => null;
}
