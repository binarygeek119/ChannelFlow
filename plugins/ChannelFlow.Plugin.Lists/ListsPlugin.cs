using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ChannelFlow.PluginSdk;

namespace ChannelFlow.Plugin.Lists;

public class ListsPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.lists", "Lists", "2.0.1", "3.0.0", Category: "catalog");
    private PluginApi? _api;
    public Task OnLoadAsync(PluginApi api) { _api = api; return Task.CompletedTask; }
    public Task OnEnableAsync() => Task.CompletedTask;
    public Task OnDisableAsync() => Task.CompletedTask;
    public void OnUnload() { }
    public Task OnConfigAsync(JsonElement config) => Task.CompletedTask;
    public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/status", () => new { status = "ok", plugin = "com.channelflow.lists" });
    }
    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => null;
}
