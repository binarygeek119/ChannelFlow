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
        if (!res.IsSuccessStatusCode)
        {
            // Try alternate endpoint
            res = await c.GetAsync(url + "/api/Library/MediaFolders");
            if (!res.IsSuccessStatusCode)
                return new List<Library>(); // Return empty instead of throwing
        }
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
            var includeTypes = ctx.MediaType switch
            {
                "album" or "artist" or "track" => "Audio,MusicAlbum,MusicArtist",
                "musicvideo" => "MusicVideo",
                "series" => "Series,Episode",
                "movie" => "Movie",
                _ => "Movie,Series,Episode"
            };
            var fetchUrl = $"{url}/Items?ParentId={ctx.LibraryId}&Recursive=true&IncludeItemTypes={includeTypes}&Fields=Genres,OfficialRating,RunTimeTicks,ProductionYear,People,ImageTags&Limit=10000";
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
                    var kind = ctx.MediaType switch
                    {
                        "album" or "artist" or "track" or "musicvideo" or "news" => ctx.MediaType,
                        _ => (type?.ToLowerInvariant() switch
                        {
                            "series" => "series",
                            "episode" => "episode",
                            "audio" or "musicalbum" => "album",
                            "musicartist" => "artist",
                            "musicvideo" => "musicvideo",
                            _ => "movie"
                        })
                    };
                    int? year = it.TryGetProperty("ProductionYear", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;
                    long? runtime = null;
                    if (it.TryGetProperty("RunTimeTicks", out var rt) && rt.ValueKind == JsonValueKind.Number)
                        runtime = rt.GetInt64() / 10000;
                    var genres = new List<string>();
                    if (it.TryGetProperty("Genres", out var g) && g.ValueKind == JsonValueKind.Array)
                        foreach (var gg in g.EnumerateArray()) if (gg.GetString() is string gs) genres.Add(gs);
                    string? rating = it.TryGetProperty("OfficialRating", out var r) ? r.GetString() : null;
                    string? overview = it.TryGetProperty("Overview", out var ov) ? ov.GetString() : null;
                    string? seriesName = it.TryGetProperty("SeriesName", out var sn) ? sn.GetString() : null;
                    string? seriesId = it.TryGetProperty("SeriesId", out var si) ? si.ToString() : null;
                    string? seasonName = it.TryGetProperty("SeasonName", out var sea) ? sea.GetString() : null;
                    string? seasonId = it.TryGetProperty("SeasonId", out var si2) ? si2.ToString() : null;
                    string? posterUrl = null;
                    if (it.TryGetProperty("ImageTags", out var imgTags) && imgTags.ValueKind == JsonValueKind.Object && imgTags.TryGetProperty("Primary", out _))
                    {
                        var imageUrl = $"{url}/Items/{id}/Images/Primary?maxHeight=300&quality=90";
                        var destDir = Path.Combine(GetImagesDir(), "posters");
                        var destPath = Path.Combine(destDir, $"{Sanitize(id)}.jpg");
                        var local = await DownloadImageAsync(c, imageUrl, destPath, "/images/posters");
                        posterUrl = local ?? imageUrl;
                    }
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
                        SourceKind: "jellyfin",
                        Overview: overview,
                        SeriesName: seriesName,
                        SeriesId: seriesId,
                        SeasonName: seasonName,
                        SeasonId: seasonId));
                }
            }
            // Sync people (cast/crew) for each item
            var allPeople = new List<PersonDto>();
            var creditsByItem = new Dictionary<string, List<(string personName, string role, string? character, string? imagePath)>>();
            if (doc.RootElement.TryGetProperty("Items", out var itemsArr))
            {
                foreach (var it in itemsArr.EnumerateArray())
                {
                    var itemId = it.TryGetProperty("Id", out var idP) ? idP.GetString() ?? "" : (it.TryGetProperty("id", out var idP2) ? idP2.GetString() ?? "" : "");
                    var matchKey = $"jellyfin:{itemId}";
                    if (!it.TryGetProperty("People", out var peopleArr) || peopleArr.ValueKind != JsonValueKind.Array) continue;
                    var credits = new List<(string, string, string?, string?)>();
                    foreach (var person in peopleArr.EnumerateArray())
                    {
                        var personName = person.TryGetProperty("Name", out var pn) ? pn.GetString() : (person.TryGetProperty("name", out var pn2) ? pn2.GetString() : null);
                        if (string.IsNullOrEmpty(personName)) continue;
                        var role = person.TryGetProperty("Type", out var pt) ? pt.GetString() ?? "Actor" : "Actor";
                        var character = person.TryGetProperty("Role", out var pr) ? pr.GetString() : null;
                        var personId = person.TryGetProperty("Id", out var pid) ? pid.GetString() : null;
                        string? imagePath = null;
                        if (!string.IsNullOrEmpty(personId))
                        {
                            bool hasImage = false;
                            if (person.TryGetProperty("ImageTags", out var pit) && pit.ValueKind == JsonValueKind.Object && pit.TryGetProperty("Primary", out _)) hasImage = true;
                            else if (person.TryGetProperty("PrimaryImageTag", out var ptag) && ptag.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ptag.GetString())) hasImage = true;
                            if (hasImage)
                            {
                                var imageUrl = $"{url}/Items/{personId}/Images/Primary?maxHeight=200&quality=90";
                                var destDir = Path.Combine(GetImagesDir(), "people");
                                var destPath = Path.Combine(destDir, $"{Sanitize(personId)}.jpg");
                                var local = await DownloadImageAsync(c, imageUrl, destPath, "/images/people");
                                imagePath = local ?? imageUrl;
                            }
                        }
                        allPeople.Add(new PersonDto(Guid.Empty, personName, imagePath));
                        credits.Add((personName, role, character, imagePath));
                    }
                    if (credits.Count > 0) creditsByItem[matchKey] = credits;
                }
            }
            // Upsert people and build name->id map
            var upsertedPeople = await ctx.Core.UpsertPeopleAsync(allPeople.DistinctBy(p => p.Name).ToList());
            var personIdMap = upsertedPeople.ToDictionary(p => p.Name, p => p.Id);
            // Link credits
            foreach (var (matchKey, credits) in creditsByItem)
            {
                var linkedCredits = credits.Select(cr =>
                    new PersonCreditDto(Guid.Empty, personIdMap.TryGetValue(cr.personName, out var pid) ? pid : Guid.Empty, matchKey, cr.role, cr.character, cr.imagePath)).ToList();
                await ctx.Core.LinkCreditsAsync(matchKey, linkedCredits);
            }

            await ctx.Core.UpsertMediaItemsAsync(items);
            added = items.Count;
        }
        catch (Exception e) { errors.Add(e.Message); }
        return new SyncReport(added, 0, 0, errors);
    }

    static string GetImagesDir()
    {
        if (Directory.Exists("/config")) return "/config/images";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "channelflow", "images");
    }

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    static async Task<string?> DownloadImageAsync(HttpClient c, string imageUrl, string destPath, string urlPrefix)
    {
        try
        {
            var res = await c.GetAsync(imageUrl);
            if (!res.IsSuccessStatusCode) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await File.WriteAllBytesAsync(destPath, bytes);
            var fileName = Path.GetFileName(destPath);
            var subDir = Path.GetFileName(Path.GetDirectoryName(destPath)!);
            return $"{urlPrefix}/{fileName}";
        }
        catch { return null; }
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
