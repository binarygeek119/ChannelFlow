using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ChannelFlow.PluginSdk;

namespace ChannelFlow.Plugin.Emergency;

public enum AlertDisplay { Off, CutIn, Ticker }
public enum WeatherSource { Auto, Us, World }

public class EmergencySettings
{
    public AlertDisplay AlertDisplay { get; set; } = AlertDisplay.Off;
    public uint CutinIntervalMinutes { get; set; } = 15;
    public uint CutinDurationSeconds { get; set; } = 20;
    public string Location { get; set; } = "";
    public WeatherSource Source { get; set; } = WeatherSource.Auto;
}

public class EmergencyPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.emergency", "Emergency Broadcast System", "3.0.0", "3.0.0", Category: "presentation",
        Permissions: new List<string> { "storage:read", "storage:write", "network:outbound", "api:routes", "ui:pages" });

    private PluginApi? _api;
    private EmergencySettings _settings = new();
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
                var s = JsonSerializer.Deserialize<EmergencySettings>(val.Value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } });
                if (s is not null) { lock (_lock) { _settings = s; } }
            }
        }
        catch { }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            EmergencySettings s;
            lock (_lock) { s = _settings; }
            var json = JsonSerializer.SerializeToElement(s, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
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
        endpoints.MapGet("/", () => { lock (_lock) { return Results.Ok(new { settings = _settings, options = new { alert_displays = new[] { "off", "cutin", "ticker" } } }); } });

        endpoints.MapPut("/", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                var s = JsonSerializer.Deserialize<EmergencySettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } });
                if (s is null) return Results.BadRequest(new { error = "invalid body" });
                s.Location = s.Location.Trim();
                // validate
                if (s.AlertDisplay == AlertDisplay.CutIn)
                {
                    if (s.CutinIntervalMinutes < 1 || s.CutinIntervalMinutes > 180)
                        return Results.BadRequest(new { error = "cut-in interval must be between 1 and 180 minutes" });
                    if (s.CutinDurationSeconds < 5 || s.CutinDurationSeconds > 120)
                        return Results.BadRequest(new { error = "cut-in duration must be between 5 and 120 seconds" });
                }
                lock (_lock) { _settings = s; }
                await SaveSettingsAsync();
                return Results.Ok(new { settings = s });
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });

        endpoints.MapGet("/test", () =>
        {
            EmergencySettings s;
            lock (_lock) { s = _settings; }
            var kind = s.AlertDisplay switch { AlertDisplay.Off => "off", AlertDisplay.CutIn => "cutin", AlertDisplay.Ticker => "ticker", _ => "off" };
            var detail = s.AlertDisplay switch
            {
                AlertDisplay.Off => "Alerts are off — no overlay will appear during programming.",
                AlertDisplay.CutIn => $"Alerts screen every {s.CutinIntervalMinutes} minutes for {s.CutinDurationSeconds} seconds; show audio ducks under the attention/end tones.",
                AlertDisplay.Ticker => "Scrolling alert text overlays the bottom of the current program.",
                _ => ""
            };
            return Results.Ok(new { result = new { ok = s.AlertDisplay != AlertDisplay.Off, display = kind, detail } });
        });

        endpoints.MapGet("/alerts", async () =>
        {
            EmergencySettings s;
            lock (_lock) { s = _settings; }
            var mode = s.AlertDisplay switch { AlertDisplay.Off => "off", AlertDisplay.CutIn => "cutin", AlertDisplay.Ticker => "ticker", _ => "off" };
            var nextCutIn = mode == "cutin" ? s.CutinIntervalMinutes * 60 : 0;
            if (string.IsNullOrWhiteSpace(s.Location))
                return Results.Ok(new { ok = false, mode, error = "Set a location to fetch alerts.", alerts = Array.Empty<object>(), next_cut_in_seconds = nextCutIn, cutin_duration_seconds = s.CutinDurationSeconds });

            // Geocode
            (string name, double lat, double lon)? place = null;
            try
            {
                // Check if coordinates
                var parts = s.Location.Split(',');
                if (parts.Length == 2 && double.TryParse(parts[0].Trim(), out var la) && double.TryParse(parts[1].Trim(), out var lo))
                    place = (s.Location.Trim(), la, lo);
                else
                {
                    using var http = new HttpClient();
                    var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(s.Location)}&count=1";
                    var geoRes = await http.GetFromJsonAsync<JsonElement>(geoUrl);
                    if (geoRes.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
                    {
                        var r = results[0];
                        place = (r.TryGetProperty("name", out var n) ? n.GetString() ?? s.Location : s.Location,
                                 r.TryGetProperty("latitude", out var la2) ? la2.GetDouble() : 0,
                                 r.TryGetProperty("longitude", out var lo2) ? lo2.GetDouble() : 0);
                    }
                }
            }
            catch { }
            if (place is null)
                return Results.Ok(new { ok = false, mode, error = $"could not find {s.Location}", alerts = Array.Empty<object>(), next_cut_in_seconds = nextCutIn, cutin_duration_seconds = s.CutinDurationSeconds });

            var (name, lat, lon) = place.Value;
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,wind_speed_10m&alerts=true&temperature_unit=fahrenheit&wind_speed_unit=mph&timezone=auto";
            try
            {
                using var http = new HttpClient();
                var body = await http.GetFromJsonAsync<JsonElement>(url);
                var alerts = new List<object>();
                if (body.TryGetProperty("alerts", out var alertsEl) && alertsEl.TryGetProperty("alert", out var alertArr) && alertArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in alertArr.EnumerateArray())
                    {
                        alerts.Add(new
                        {
                            @event = a.TryGetProperty("event", out var e) ? e.GetString() : null,
                            headline = a.TryGetProperty("headline", out var h) ? h.GetString() : null,
                            description = a.TryGetProperty("description", out var d) ? d.GetString() : null,
                            severity = a.TryGetProperty("severity", out var se) ? se.GetString() : null,
                            urgency = a.TryGetProperty("urgency", out var u) ? u.GetString() : null,
                            starts_at = a.TryGetProperty("start", out var st) ? st.GetString() : null,
                            ends_at = a.TryGetProperty("end", out var en) ? en.GetString() : null,
                        });
                    }
                }
                var current = body.TryGetProperty("current", out var c) ? c : default;
                return Results.Ok(new { ok = true, mode, location = new { name, latitude = lat, longitude = lon }, alerts, count = alerts.Count, next_cut_in_seconds = nextCutIn, cutin_duration_seconds = s.CutinDurationSeconds, current });
            }
            catch (Exception e)
            {
                return Results.Ok(new { ok = false, mode, error = "could not reach the weather service: " + e.Message, alerts = Array.Empty<object>(), next_cut_in_seconds = nextCutIn, cutin_duration_seconds = s.CutinDurationSeconds });
            }
        });
    }

    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => null;
}
