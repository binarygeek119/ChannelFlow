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