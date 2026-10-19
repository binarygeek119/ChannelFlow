using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using ChannelFlow.PluginSdk;

namespace ChannelFlow.Core;

public class PluginManager
{
    private readonly Dictionary<string, LoadedPlugin> _plugins = new();
    private readonly IServiceProvider _services;

    public PluginManager(IServiceProvider services) => _services = services;

    public async Task LoadAsync(string id, string dllPath)
    {
        if (_plugins.ContainsKey(id)) return;
        var alc = new AssemblyLoadContext(id, isCollectible: true);
        var assembly = alc.LoadFromAssemblyPath(dllPath);
        var pluginType = assembly.GetTypes().FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);
        if (pluginType is null) throw new InvalidOperationException($"No IPlugin in {dllPath}");
        var plugin = (IPlugin)Activator.CreateInstance(pluginType)!;
        var api = new PluginApi
        {
            Id = id,
            Storage = new DbPluginStorage(_services, id),
            Http = new HttpClient(),
            BaseVersion = "3.0.0",
            Dir = Path.GetDirectoryName(dllPath)!,
            Logger = new ConsolePluginLogger(id),
            Core = _services.GetService(typeof(ICoreData)) as ICoreData ?? new NoCoreData(),
            Tasks = new NoopTaskHost(_services.GetService(typeof(TaskTracker)) as TaskTracker),
            // debug: Console.WriteLine($"[pm] TaskTracker resolved: {_services.GetService(typeof(TaskTracker)) is not null}"),
        };
        await plugin.OnLoadAsync(api);
        _plugins[id] = new LoadedPlugin(plugin, alc);
    }

    public IEnumerable<IPlugin> Plugins => _plugins.Values.Select(p => p.Plugin);
    public IPlugin? Get(string id) => _plugins.TryGetValue(id, out var p) ? p.Plugin : null;

    public async Task EnableAsync(string id)
    {
        if (_plugins.TryGetValue(id, out var p)) await p.Plugin.OnEnableAsync();
    }

    public async Task DisableAsync(string id)
    {
        if (_plugins.TryGetValue(id, out var p)) await p.Plugin.OnDisableAsync();
    }

    public void Unload(string id)
    {
        if (_plugins.Remove(id, out var p)) p.Alc.Unload();
    }

    private record LoadedPlugin(IPlugin Plugin, AssemblyLoadContext Alc);
}

// Minimal stubs for compilation
public class InMemoryPluginStorage : IPluginStorage
{
    private readonly Dictionary<string, JsonElement> _store = new();
    public Task<JsonElement?> GetAsync(string key) => Task.FromResult(_store.TryGetValue(key, out var v) ? v : (JsonElement?)null);
    public Task SetAsync(string key, JsonElement value) { _store[key] = value; return Task.CompletedTask; }
    public Task DeleteAsync(string key) { _store.Remove(key); return Task.CompletedTask; }
}

public class ConsolePluginLogger : IPluginLogger
{
    private readonly string _id;
    public ConsolePluginLogger(string id) => _id = id;
    public void Info(string m) => Console.WriteLine($"[{_id}] INFO: {m}");
    public void Warn(string m) => Console.WriteLine($"[{_id}] WARN: {m}");
    public void Error(string m) => Console.WriteLine($"[{_id}] ERROR: {m}");
}

public class NoCoreData : ICoreData
{
    public Task<List<CoreChannel>> ChannelsAsync() => Task.FromResult(new List<CoreChannel>());
    public Task<List<CatalogRow>> CatalogAsync(CatalogFilter f) => Task.FromResult(new List<CatalogRow>());
    public Task<ChatResponse> ChatAsync(ChatRequest r) => Task.FromResult(new ChatResponse("", "", new()));
    public Task FileForAsync(string mk) => Task.CompletedTask;
    public Task SetChannelGuideAsync(string cid, List<GuideProgram> p) => Task.CompletedTask;
    public Task SetChannelLineupAsync(string cid, List<LineupSlot> s) => Task.CompletedTask;
    public Task GeneratePlayoutAsync(string cid) => Task.CompletedTask;
    public Task UpsertMediaItemsAsync(List<MediaItemDto> items) => Task.CompletedTask;
    public Task RemoveMediaItemsAsync(string conn, string lib) => Task.CompletedTask;
    public Task<List<PersonCreditDto>> GetCreditsForMediaAsync(string mk) => Task.FromResult(new List<PersonCreditDto>());
    public Task<PersonCreditDto> AddCreditAsync(PersonCreditDto c) => Task.FromResult(c);
    public Task RemoveCreditAsync(Guid id) => Task.CompletedTask;
}

public class NoopTaskHost : IPluginTaskHost
{
    private readonly TaskTracker? _tracker;
    public NoopTaskHost(TaskTracker? tracker = null) { _tracker = tracker; }
    public void Report(string k, string t, string? l, long? c, long? tot, string status = "running", string? detail = null) { _tracker?.Report(k, t, l, c ?? 0, tot ?? 0, status, detail); }
}

// Discovery extension (appended)

// Database-backed plugin storage
public class DbPluginStorage : IPluginStorage
{
    private readonly IServiceProvider _sp;
    private readonly string _pluginId;
    public DbPluginStorage(IServiceProvider sp, string pluginId) { _sp = sp; _pluginId = pluginId; }

    private string FullKey(string key) => $"{_pluginId}:{key}";

    public async Task<JsonElement?> GetAsync(string key)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlow.Database.ChannelFlowDbContext>();
        var s = await db.Settings.FindAsync(FullKey(key));
        if (s is null || string.IsNullOrEmpty(s.Value)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(s.Value); }
        catch { return null; }
    }

    public async Task SetAsync(string key, JsonElement value)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlow.Database.ChannelFlowDbContext>();
        var fk = FullKey(key);
        var s = await db.Settings.FindAsync(fk);
        var json = value.GetRawText();
        if (s is null) db.Settings.Add(new ChannelFlow.Database.AppSetting { Key = fk, Value = json });
        else s.Value = json;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string key)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlow.Database.ChannelFlowDbContext>();
        var s = await db.Settings.FindAsync(FullKey(key));
        if (s is not null) { db.Settings.Remove(s); await db.SaveChangesAsync(); }
    }
}
