namespace ChannelFlow.PluginSdk;

public interface IPlugin
{
    PluginManifest Metadata { get; }

    Task OnLoadAsync(PluginApi Api);
    Task OnEnableAsync();
    Task OnDisableAsync();
    void OnUnload();
    Task OnConfigAsync(System.Text.Json.JsonElement Config);

    // Routes are mapped by the host under /api/plugins/{id}
    void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder Endpoints);

    IEnumerable<UiContribution> UiContributions { get; }
    PluginHealth Health { get; }

    // Optional media source
    IMediaSource? MediaSource { get; }
}

public record PluginManifest(
    string Id,
    string Name,
    string Version = "",
    string MinBaseVersion = "",
    string MaxBaseVersion = "",
    string MinApi = "",
    string ForceMinApiVersion = "",
    string Author = "",
    string Description = "",
    string Homepage = "",
    string Category = "",
    string ImageUrl = "",
    List<string> Permissions = default!,
    List<string> Dependencies = default!,
    Dictionary<string, string> Entrypoint = default!,
    List<UiContribution> UiContributions = default!
);

public record UiContribution(string Type, string Id, string Path, string Title, string Icon = "", string Section = "main");

public record PluginHealth(bool Ok, string Detail = "");

public class PluginApi
{
    public required string Id { get; init; }
    public required IPluginStorage Storage { get; init; }
    public required HttpClient Http { get; init; }
    public required string BaseVersion { get; init; }
    public required string Dir { get; init; }
    public required IPluginLogger Logger { get; init; }
    public required ICoreData Core { get; init; }
    public IPluginDatabase? Database { get; init; }
    public IPluginTaskHost? Tasks { get; init; }
    public PluginWeb Web { get; init; } = new();
}

public class PluginWeb
{
    public Dictionary<string, byte[]> Assets { get; } = new();
}
