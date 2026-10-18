using System.Text.Json;
using System.Text.Json.Serialization;
using ChannelFlow.PluginSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ChannelFlow.Plugin.OffAir;

public enum DisplayMode { SlateImage, ColorBars, Static }
public enum SlateVariant { Usa, International }
public enum AudioMode { BackgroundMusic, WhiteNoise, Silence, BeepTone }
public enum MusicSource { AllMusicLibraries, NamedLibrary, LocalPacks }

public class OffAirSettings
{
    public DisplayMode DisplayMode { get; set; } = DisplayMode.SlateImage;
    public SlateVariant SlateVariant { get; set; } = SlateVariant.Usa;
    public AudioMode AudioMode { get; set; } = AudioMode.BackgroundMusic;
    public MusicSource MusicSource { get; set; } = MusicSource.AllMusicLibraries;
    public string MusicLibraryName { get; set; } = "";
    public string MusicLibraryId { get; set; } = "";
}

public class OffAirPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.offair", "Off Air", "3.0.0", "3.0.0", Category: "presentation",
        Permissions: new List<string> { "storage:read", "storage:write", "network:outbound", "api:routes", "ui:pages" });

    private PluginApi? _api;
    private OffAirSettings _settings = new();
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
                var s = JsonSerializer.Deserialize<OffAirSettings>(val.Value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } });
                if (s is not null) { lock (_lock) { _settings = s; } }
            }
        }
        catch { }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            OffAirSettings s;
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
        endpoints.MapGet("/", () =>
        {
            lock (_lock)
            {
                var customSlates = new Dictionary<string, string?>();
                foreach (var v in new[] { "usa", "international" })
                {
                    customSlates[v] = FindCustomSlate(v);
                }
                return Results.Ok(new
                {
                    settings = _settings,
                    options = new
                    {
                        display_modes = new[] { "slate_image", "color_bars", "static" },
                        slate_variants = new[] { "usa", "international" },
                        audio_modes = new[] { "background_music", "white_noise", "silence", "beep_tone" },
                        music_sources = new[] { "all_music_libraries", "named_library", "local_packs" }
                    },
                    custom_slates = customSlates
                });
            }
        });

        endpoints.MapPut("/", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                var s = JsonSerializer.Deserialize<OffAirSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } });
                if (s is null) return Results.BadRequest(new { error = "invalid body" });
                // validate
                if (s.AudioMode == AudioMode.BackgroundMusic && s.MusicSource == MusicSource.NamedLibrary
                    && string.IsNullOrWhiteSpace(s.MusicLibraryName) && string.IsNullOrWhiteSpace(s.MusicLibraryId))
                    return Results.BadRequest(new { error = "choose a music library, or pick another music source" });
                lock (_lock) { _settings = s; }
                await SaveSettingsAsync();
                return Results.Ok(new { settings = s });
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });

        // Upload custom slate
        endpoints.MapPost("/slates/{variant}", async (string variant, HttpRequest req) =>
        {
            if (variant is not ("usa" or "international"))
                return Results.BadRequest(new { error = "variant must be usa or international" });
            var bytes = await ReadBodyBytes(req);
            if (bytes.Length == 0) return Results.BadRequest(new { error = "an image file is required" });
            if (bytes.Length > 2 * 1024 * 1024) return Results.BadRequest(new { error = "the slate image is larger than 2 MB" });

            string ext;
            if (bytes[0] == 0x89 && bytes[1] == 0x50) ext = "png";
            else if (bytes[0] == 0xFF && bytes[1] == 0xD8) ext = "jpg";
            else return Results.BadRequest(new { error = "the slate must be a PNG or JPEG image" });

            var dir = Path.Combine(_api!.Dir, "slates");
            Directory.CreateDirectory(dir);
            // Remove old
            foreach (var old in new[] { "png", "jpg" })
                if (old != ext) try { File.Delete(Path.Combine(dir, $"{variant}.{old}")); } catch { }
            await File.WriteAllBytesAsync(Path.Combine(dir, $"{variant}.{ext}"), bytes);
            return Results.Ok(new { variant, custom_slates = GetCustomSlates(dir) });
        });

        // Delete custom slate
        endpoints.MapDelete("/slates/{variant}", (string variant) =>
        {
            if (variant is not ("usa" or "international"))
                return Results.BadRequest(new { error = "variant must be usa or international" });
            var dir = Path.Combine(_api!.Dir, "slates");
            foreach (var ext in new[] { "png", "jpg" })
                try { File.Delete(Path.Combine(dir, $"{variant}.{ext}")); } catch { }
            return Results.Ok(new { custom_slates = GetCustomSlates(dir) });
        });

        // Serve slate image
        endpoints.MapGet("/slates/{variant}/image", (string variant, int aspect) =>
        {
            var dir = Path.Combine(_api!.Dir, "slates");
            var custom = FindCustomSlatePath(dir, variant);
            if (custom is not null)
            {
                var mime = custom.EndsWith(".png") ? "image/png" : "image/jpeg";
                return Results.File(custom, mime);
            }
            // Redirect to stock image
            var wide = variant == "usa" ? "offline_usa_16_9.jpg" : "offline_world_16_9.jpg";
            var square = variant == "usa" ? "offline_usa_4_3.jpg" : "offline_world_4_3.jpg";
            var file = aspect == 1 ? square : wide;
            return Results.Redirect($"/plugin/com.channelflow.offair/web/media/{file}");
        });
    }

    private static async Task<byte[]> ReadBodyBytes(HttpRequest req)
    {
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    private string? FindCustomSlate(string variant)
    {
        var dir = Path.Combine(_api!.Dir, "slates");
        foreach (var ext in new[] { "png", "jpg" })
        {
            var path = Path.Combine(dir, $"{variant}.{ext}");
            if (File.Exists(path)) return Path.GetFileName(path);
        }
        return null;
    }

    private string? FindCustomSlatePath(string dir, string variant)
    {
        foreach (var ext in new[] { "png", "jpg" })
        {
            var path = Path.Combine(dir, $"{variant}.{ext}");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private Dictionary<string, string?> GetCustomSlates(string dir)
    {
        var map = new Dictionary<string, string?>();
        foreach (var v in new[] { "usa", "international" })
        {
            map[v] = FindCustomSlate(v);
        }
        return map;
    }

    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => null;
}
