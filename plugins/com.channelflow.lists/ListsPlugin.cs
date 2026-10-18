using System.Text.Json;
using ChannelFlow.PluginSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ChannelFlow.Plugin.Lists;

public class ListsPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.lists", "Lists", "3.0.0", "3.0.0", Category: "catalog",
        Permissions: new List<string> { "storage:read", "storage:write", "api:routes", "ui:pages" });

    private PluginApi? _api;

    private async Task<JsonElement> LoadDocAsync()
    {
        try
        {
            var val = await _api!.Storage.GetAsync("lists");
            if (val.HasValue) return val.Value;
        }
        catch { }
        return JsonSerializer.SerializeToElement(new { lists = new List<object>() });
    }

    private async Task SaveDocAsync(JsonElement doc)
    {
        await _api!.Storage.SetAsync("lists", doc);
    }

    public Task OnLoadAsync(PluginApi api) { _api = api; return Task.CompletedTask; }
    public Task OnEnableAsync() => Task.CompletedTask;
    public Task OnDisableAsync() => Task.CompletedTask;
    public void OnUnload() { }
    public Task OnConfigAsync(JsonElement config) => Task.CompletedTask;

    public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/lists", async () =>
        {
            var doc = await LoadDocAsync();
            var lists = doc.TryGetProperty("lists", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().ToList() : new List<JsonElement>();
            return Results.Ok(new { lists });
        });

        endpoints.MapPost("/lists", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { error = "name is required" });

                var store = await LoadDocAsync();
                var lists = store.TryGetProperty("lists", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().ToList() : new List<JsonElement>();
                var newList = new { id = Guid.NewGuid().ToString(), name, created_at = DateTime.UtcNow.ToString("o"), items = new List<object>() };
                var newListEl = JsonSerializer.SerializeToElement(newList);
                lists.Add(newListEl);
                var newDoc = JsonSerializer.SerializeToElement(new { lists });
                await SaveDocAsync(newDoc);
                return Results.Ok(newList);
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });

        endpoints.MapDelete("/lists/{id}", async (string id) =>
        {
            var store = await LoadDocAsync();
            var lists = store.TryGetProperty("lists", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().ToList() : new List<JsonElement>();
            var before = lists.Count;
            lists = lists.Where(x => !(x.TryGetProperty("id", out var i) && i.GetString() == id)).ToList();
            if (lists.Count == before) return Results.NotFound(new { error = "no such list" });
            var newDoc = JsonSerializer.SerializeToElement(new { lists });
            await SaveDocAsync(newDoc);
            return Results.Ok(new { ok = true });
        });
    }

    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => null;
}
