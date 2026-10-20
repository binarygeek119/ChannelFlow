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
        // Resolve a user id for collection child lookups
        string? userId = null;
        try
        {
            var usersRes = await c.GetAsync($"{url}/Users");
            if (usersRes.IsSuccessStatusCode)
            {
                var usersJson = await usersRes.Content.ReadAsStringAsync();
                using var udoc = JsonDocument.Parse(usersJson);
                if (udoc.RootElement.ValueKind == JsonValueKind.Array && udoc.RootElement.GetArrayLength() > 0)
                    userId = udoc.RootElement[0].TryGetProperty("Id", out var uid) ? uid.GetString() : null;
            }
        }
        catch { }
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
            int totalItems = 0, processed = 0;
            if (doc.RootElement.TryGetProperty("Items", out var arr))
            {
                totalItems = arr.GetArrayLength();
                foreach (var it in arr.EnumerateArray())
                {
                    if (ctx.Tasks is not null && !string.IsNullOrEmpty(ctx.TaskKey))
                    {
                        var displayName = ctx.MediaType switch { "movie" => "Movies", "series" => "TV Shows", "album" => "Music", "musicvideo" => "Music Videos", _ => ctx.MediaType };
                        ctx.Tasks.Report(ctx.TaskKey, $"Syncing {displayName}", $"{processed} of {totalItems}", processed, totalItems, "running");
                    }
                    processed++;
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
                        posterUrl = $"{url}/Items/{id}/Images/Primary?maxHeight=300&quality=90";
                    }
                    var webUrl = $"{url}/web/index.html#!/details?id={id}";
                    var collectionItems = new List<string>();
                    if (type?.Equals("BoxSet", StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrEmpty(userId))
                    {
                        try
                        {
                            var childRes = await c.GetAsync($"{url}/Users/{userId}/Items?ParentId={id}&Recursive=false");
                            if (childRes.IsSuccessStatusCode)
                            {
                                var childJson = await childRes.Content.ReadAsStringAsync();
                                using var cdoc = JsonDocument.Parse(childJson);
                                if (cdoc.RootElement.TryGetProperty("Items", out var citems))
                                {
                                    foreach (var ci in citems.EnumerateArray())
                                    {
                                        var cid = ci.TryGetProperty("Id", out var cip) ? cip.GetString() : null;
                                        if (!string.IsNullOrEmpty(cid)) collectionItems.Add($"jellyfin:{cid}");
                                    }
                                }
                            }
                        }
                        catch { }
                    }
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
                        SeasonId: seasonId,
                        CollectionItems: collectionItems));
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
                                imagePath = $"{url}/Items/{personId}/Images/Primary?maxHeight=200&quality=90";
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

            // Fetch children of BoxSets/collections and add them as items
            if (!string.IsNullOrEmpty(userId))
            {
                var boxSets = items.Where(i => i.Kind == "movie" && i.Title.Contains("Collection")).ToList();
                // We don't have a flag; fetch children for items whose Jellyfin type is BoxSet.
                // Re-iterate original arr to find BoxSets.
            }
            // Re-scan arr for BoxSet items and fetch their children
            if (!string.IsNullOrEmpty(userId) && doc.RootElement.TryGetProperty("Items", out var arr2))
            {
                foreach (var it in arr2.EnumerateArray())
                {
                    var type = it.TryGetProperty("Type", out var t) ? t.GetString() : "";
                    if (!type?.Equals("BoxSet", StringComparison.OrdinalIgnoreCase) == true) continue;
                    var id = it.TryGetProperty("Id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(id)) continue;
                    try
                    {
                        var childRes = await c.GetAsync($"{url}/Users/{userId}/Items?ParentId={id}&Recursive=false");
                        if (!childRes.IsSuccessStatusCode) continue;
                        var childJson = await childRes.Content.ReadAsStringAsync();
                        using var cdoc = JsonDocument.Parse(childJson);
                        if (!cdoc.RootElement.TryGetProperty("Items", out var citems)) continue;
                        foreach (var ci in citems.EnumerateArray())
                        {
                            var cid = ci.TryGetProperty("Id", out var cip) ? cip.GetString() ?? "" : "";
                            var cname = ci.TryGetProperty("Name", out var cnp) ? cnp.GetString() ?? cid : cid;
                            if (string.IsNullOrEmpty(cid)) continue;
                            if (items.Any(x => x.MatchKey == $"jellyfin:{cid}")) continue; // already present
                            int? cyear = ci.TryGetProperty("ProductionYear", out var cy) && cy.ValueKind == JsonValueKind.Number ? cy.GetInt32() : null;
                            string? cposter = null;
                            if (ci.TryGetProperty("ImageTags", out var cit) && cit.ValueKind == JsonValueKind.Object && cit.TryGetProperty("Primary", out _))
                                cposter = $"{url}/Items/{cid}/Images/Primary?maxHeight=300&quality=90";
                            items.Add(new MediaItemDto(
                                MatchKey: $"jellyfin:{cid}",
                                Kind: "movie",
                                Title: cname,
                                Year: cyear,
                                RuntimeMs: null,
                                Genres: new List<string>(),
                                OfficialRating: null,
                                Tags: new List<string>(),
                                ConnectionId: ctx.ConnectionId,
                                LibraryId: ctx.LibraryId,
                                PosterUrl: cposter,
                                WebUrl: $"{url}/web/index.html#!/details?id={cid}",
                                SourceKind: "jellyfin",
                                Overview: null));
                        }
                    }
                    catch { }
                }
            }
            await ctx.Core.UpsertMediaItemsAsync(items);
            added = items.Count;
        }
        catch (Exception e) { errors.Add(e.Message); }
        return new SyncReport(added, 0, 0, errors);
    }

    public async Task<SyncReport> DownloadPostersAsync(SyncContext ctx)
    {
        var conn = ctx.Connection;
        if (conn is null) return new SyncReport(0, 0, 0, new List<string> { "No connection" });
        var url = conn.Config.TryGetValue("url", out var u) ? u.TrimEnd('/') : "";
        var apiKey = ctx.ApiKey;
        using var c = Client(apiKey);
        int done = 0;
        var errors = new List<string>();
        try
        {
            var all = await ctx.Core.GetAllMediaItemsAsync(ctx.ConnectionId);
            var toUpdate = new List<MediaItemDto>();
            int pTotal = all.Count, pDone = 0;
            foreach (var item in all)
            {
                pDone++;
                if (ctx.Tasks is not null && !string.IsNullOrEmpty(ctx.TaskKey))
                    ctx.Tasks.Report(ctx.TaskKey, "Downloading posters", $"{pDone} of {pTotal}", pDone, pTotal, "running");
                if (string.IsNullOrEmpty(item.PosterUrl) || !item.PosterUrl.StartsWith("http")) continue;
                var id = item.MatchKey.StartsWith("jellyfin:") ? item.MatchKey.Substring("jellyfin:".Length) : null;
                if (string.IsNullOrEmpty(id)) continue;
                var destPath = Path.Combine(GetImagesDir(), "posters", $"{Sanitize(id)}.jpg");
                var local = await DownloadImageAsync(c, item.PosterUrl, destPath, "/images/posters");
                if (local is not null)
                {
                    var updated = item with { PosterUrl = local };
                    toUpdate.Add(updated);
                    done++;
                }
            }
            if (toUpdate.Count > 0) await ctx.Core.UpsertMediaItemsAsync(toUpdate);
        }
        catch (Exception e) { errors.Add(e.Message); }
        return new SyncReport(done, 0, 0, errors);
    }

    public async Task<SyncReport> DownloadPeopleImagesAsync(SyncContext ctx)
    {
        var conn = ctx.Connection;
        if (conn is null) return new SyncReport(0, 0, 0, new List<string> { "No connection" });
        var url = conn.Config.TryGetValue("url", out var u) ? u.TrimEnd('/') : "";
        var apiKey = ctx.ApiKey;
        using var c = Client(apiKey);
        int done = 0;
        var errors = new List<string>();
        try
        {
            var all = await ctx.Core.GetAllPeopleAsync();
            var toUpdate = new List<PersonDto>();
            int pTotal = all.Count, pDone = 0;
            foreach (var person in all)
            {
                pDone++;
                if (ctx.Tasks is not null && !string.IsNullOrEmpty(ctx.TaskKey))
                    ctx.Tasks.Report(ctx.TaskKey, "Downloading people images", $"{pDone} of {pTotal}", pDone, pTotal, "running");
                if (string.IsNullOrEmpty(person.ImagePath) || !person.ImagePath.StartsWith("http")) continue;
                // person name isn't an id; we need the jellyfin id. The ImagePath URL contains the id.
                // Extract id from URL: .../Items/{id}/Images/Primary...
                var imgPath = person.ImagePath;
                var marker = "/Items/";
                var idx = imgPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var rest = imgPath.Substring(idx + marker.Length);
                var slash = rest.IndexOf('/');
                var id = slash > 0 ? rest.Substring(0, slash) : rest;
                if (string.IsNullOrEmpty(id)) continue;
                var destPath = Path.Combine(GetImagesDir(), "people", $"{Sanitize(id)}.jpg");
                var local = await DownloadImageAsync(c, imgPath, destPath, "/images/people");
                if (local is not null)
                {
                    toUpdate.Add(person with { ImagePath = local });
                    done++;
                }
            }
            if (toUpdate.Count > 0) await ctx.Core.UpsertPeopleAsync(toUpdate);
        }
        catch (Exception e) { errors.Add(e.Message); }
        return new SyncReport(done, 0, 0, errors);
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
            // Skip if already downloaded
            if (File.Exists(destPath))
            {
                var fileName = Path.GetFileName(destPath);
                return $"{urlPrefix}/{fileName}";
            }
            var res = await c.GetAsync(imageUrl);
            if (!res.IsSuccessStatusCode) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await File.WriteAllBytesAsync(destPath, bytes);
            var fileName2 = Path.GetFileName(destPath);
            return $"{urlPrefix}/{fileName2}";
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
