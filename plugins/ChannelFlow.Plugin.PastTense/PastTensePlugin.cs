using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ChannelFlow.PluginSdk;

namespace ChannelFlow.Plugin.PastTense;

public class PastTenseMediaSource : IMediaSource
{
    public string TypeId => "pasttense";
    public string DisplayName => "Past Tense News";
    public List<FieldSpec> ConnectionFields => new()
    {
        FieldSpec.Text("path", "Path to news folder", Required: true)
    };
    public List<MediaType> SupportedMedia => new() { MediaType.Movie };

    public Task<TestResult> TestConnectionAsync(Connection connection, string apiKey)
    {
        var path = connection.Config.TryGetValue("path", out var p) ? p : "";
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult(new TestResult(false, "Path is required"));
        if (!Directory.Exists(path)) return Task.FromResult(new TestResult(false, "Directory not found: " + path));
        return Task.FromResult(new TestResult(true, "OK"));
    }

    public Task<List<Library>> ListLibrariesAsync(Connection connection, string apiKey)
    {
        var path = connection.Config.TryGetValue("path", out var p) ? p : "";
        var libs = new List<Library>();
        if (Directory.Exists(path))
        {
            libs.Add(new Library(path, "Past Tense News", MediaType.Movie));
        }
        return Task.FromResult(libs);
    }

    public Task<SyncReport> SyncLibraryAsync(SyncContext ctx)
    {
        if (ctx.Connection is null) return Task.FromResult(new SyncReport(0, 0, 0, new List<string> { "No connection" }));
        var path = ctx.Connection.Config.TryGetValue("path", out var pp) ? pp : "";
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return Task.FromResult(new SyncReport(0, 0, 0, new List<string> { "Path not found" }));

        int added = 0;
        var errors = new List<string>();
        var items = new List<MediaItemDto>();
        try
        {
            var eventDirs = Directory.GetDirectories(path);
            if (eventDirs.Length == 0)
            {
                eventDirs = new[] { path };
            }
            foreach (var eventDir in eventDirs)
            {
                var eventName = eventDir == path ? "News" : Path.GetFileName(eventDir);
                var eventKey = $"pasttense:{Sanitize(eventName)}";
                var matchKey = eventKey;
                var videoFiles = (eventDir == path)
                    ? Directory.GetFiles(path).Where(IsMediaFile).ToList()
                    : Directory.GetFiles(eventDir).Where(IsMediaFile).ToList();

                var videos = new List<string>();
                foreach (var vf in videoFiles)
                {
                    var videoName = Path.GetFileNameWithoutExtension(vf);
                    var videoKey = $"{eventKey}:{Sanitize(videoName)}";
                    videos.Add(videoKey);
                    items.Add(new MediaItemDto(
                        MatchKey: videoKey,
                        Kind: "episode",
                        Title: videoName,
                        Year: null,
                        RuntimeMs: null,
                        Genres: new List<string>(),
                        OfficialRating: null,
                        Tags: new List<string>(),
                        ConnectionId: ctx.ConnectionId,
                        LibraryId: ctx.LibraryId,
                        PosterUrl: null,
                        WebUrl: vf,
                        SourceKind: "pasttense",
                        Overview: null,
                        SeriesName: eventName,
                        SeriesId: eventKey));
                }
                items.Add(new MediaItemDto(
                    MatchKey: matchKey,
                    Kind: "news",
                    Title: eventName,
                    Year: null,
                    RuntimeMs: null,
                    Genres: new List<string>(),
                    OfficialRating: null,
                    Tags: new List<string>(),
                    ConnectionId: ctx.ConnectionId,
                    LibraryId: ctx.LibraryId,
                    PosterUrl: null,
                    WebUrl: null,
                    SourceKind: "pasttense",
                    Overview: null));
            }
            ctx.Core.UpsertMediaItemsAsync(items).Wait();
            added = items.Count;
        }
        catch (Exception e) { errors.Add(e.Message); }
        return Task.FromResult(new SyncReport(added, 0, 0, errors));
    }

    public Task<SyncReport> DownloadPostersAsync(SyncContext ctx) => Task.FromResult(new SyncReport(0, 0, 0, new List<string>()));
    public Task<SyncReport> DownloadPeopleImagesAsync(SyncContext ctx) => Task.FromResult(new SyncReport(0, 0, 0, new List<string>()));

    public async Task<SyncReport> ExtractThumbnailsAsync(SyncContext ctx)
    {
        int done = 0;
        var errors = new List<string>();
        try
        {
            var all = await ctx.Core.GetAllMediaItemsAsync(ctx.ConnectionId);
            var videos = all.Where(m => m.Kind == "episode" && m.SourceKind == "pasttense" && !string.IsNullOrEmpty(m.WebUrl)).ToList();
            var imagesDir = GetImagesDir();
            Directory.CreateDirectory(Path.Combine(imagesDir, "thumbnails"));
            foreach (var v in videos)
            {
                try
                {
                    var videoPath = v.WebUrl!;
                    if (!File.Exists(videoPath)) continue;
                    // Get duration
                    var dur = GetDuration(videoPath);
                    if (dur <= 0) continue;
                    // Try timestamps at 10%, 15%, 20%, 25%, 30%
                    var timestamps = new[] { 0.10, 0.15, 0.20, 0.25, 0.30 }
                        .Select(p => dur * p).ToList();
                    string? chosenPath = null;
                    foreach (var ts in timestamps)
                    {
                        var tmpPath = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid()}.jpg");
                        var ok = await ExtractFrameAsync(videoPath, ts, tmpPath);
                        if (!ok || !File.Exists(tmpPath)) continue;
                        // Check not black or white
                        if (IsBlackOrWhite(tmpPath)) { File.Delete(tmpPath); continue; }
                        // Save as thumbnail
                        var safeId = Sanitize(v.MatchKey);
                        var destPath = Path.Combine(imagesDir, "thumbnails", $"{safeId}.jpg");
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        File.Copy(tmpPath, destPath, true);
                        File.Delete(tmpPath);
                        chosenPath = $"/images/thumbnails/{safeId}.jpg";
                        break;
                    }
                    if (chosenPath is not null)
                    {
                        // Update the media item's PosterUrl
                        var updated = v with { PosterUrl = chosenPath };
                        await ctx.Core.UpsertMediaItemsAsync(new List<MediaItemDto> { updated });
                        done++;
                    }
                }
                catch (Exception e) { errors.Add($"{v.Title}: {e.Message}"); }
            }
        }
        catch (Exception e) { errors.Add(e.Message); }
        return new SyncReport(done, 0, 0, errors);
    }

    private static double GetDuration(string videoPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffprobe", $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return 0;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dur))
                return dur;
        }
        catch { }
        return 0;
    }

    private static async Task<bool> ExtractFrameAsync(string videoPath, double timestamp, string outputPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg", $"-ss {timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i \"{videoPath}\" -vframes 1 -q:v 2 \"{outputPath}\" -y")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0 && File.Exists(outputPath);
        }
        catch { return false; }
    }

    private static bool IsBlackOrWhite(string imagePath)
    {
        try
        {
            // Use ImageMagick to get mean brightness (0-1)
            var psi = new System.Diagnostics.ProcessStartInfo("magick", $"\"{imagePath}\" -format \"%[fx:mean]\" info:")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mean))
            {
                // Pure black: mean < 0.05, pure white: mean > 0.95
                return mean < 0.05 || mean > 0.95;
            }
        }
        catch { }
        return false;
    }

    private static string GetImagesDir()
    {
        if (Directory.Exists("/config")) return "/config/images";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "channelflow", "images");
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(" ", "_");
    }

    private static bool IsMediaFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".mpg" or ".mpeg" or ".webm" or ".m4v";
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(" ", "_");
    }
}

public class PastTensePlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.pasttense", "Past Tense News", "3.0.0", "3.0.0", Category: "media-source");
    private PluginApi? _api;
    private PastTenseMediaSource? _ms;
    public Task OnLoadAsync(PluginApi api) { _api = api; _ms = new PastTenseMediaSource(); return Task.CompletedTask; }
    public Task OnEnableAsync() => Task.CompletedTask;
    public Task OnDisableAsync() => Task.CompletedTask;
    public void OnUnload() { }
    public Task OnConfigAsync(JsonElement config) => Task.CompletedTask;
    public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/status", () => new { status = "ok", plugin = "com.channelflow.pasttense" });
    }
    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => _ms;
}