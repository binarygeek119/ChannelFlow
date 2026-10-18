using System.Text.Json;
using System.Text.Json.Serialization;
using ChannelFlow.PluginSdk;

namespace ChannelFlow.Core;

public static class PluginDiscovery
{
    // Base API version — bump when breaking plugin ABI changes
    public const string BaseApiVersion = "3.0.0";

    public static async Task LoadAllAsync(PluginManager pm, string pluginsDir, IServiceProvider sp)
    {
        if (!Directory.Exists(pluginsDir)) return;
        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var json = await File.ReadAllTextAsync(manifestPath);
                var manifest = JsonSerializer.Deserialize<PluginManifestJson>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest is null || string.IsNullOrEmpty(manifest.Entrypoint?.Dll)) continue;

                var id = manifest.Id ?? Path.GetFileName(dir);

                // force_min_api_version: hard block — plugin refuses to load on older bases
                if (!string.IsNullOrEmpty(manifest.ForceMinApiVersion))
                {
                    if (CompareVersions(BaseApiVersion, manifest.ForceMinApiVersion) < 0)
                    {
                        Console.WriteLine($"[discovery] BLOCKED {id}: requires base API >= {manifest.ForceMinApiVersion} (base is {BaseApiVersion})");
                        continue;
                    }
                }

                // min_api: soft block — plugin may not work correctly
                if (!string.IsNullOrEmpty(manifest.MinApi))
                {
                    if (CompareVersions(BaseApiVersion, manifest.MinApi) < 0)
                    {
                        Console.WriteLine($"[discovery] skipping {id}: requires API {manifest.MinApi} (base is {BaseApiVersion})");
                        continue;
                    }
                }

                var dllPath = Path.Combine(dir, manifest.Entrypoint.Dll);
                if (!File.Exists(dllPath))
                {
                    Console.WriteLine($"[discovery] missing dll for {id}: {dllPath}");
                    continue;
                }

                await pm.LoadAsync(id, dllPath);
                await pm.EnableAsync(id);
                Console.WriteLine($"[discovery] loaded {id} v{manifest.Version} (minApi={manifest.MinApi}, forceMinApi={manifest.ForceMinApiVersion})");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[discovery] failed {dir}: {e.Message}");
            }
        }
    }

    private static int CompareVersions(string a, string b)
    {
        // Simple semver comparison (major.minor.patch)
        var pa = a.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < pa.Length ? pa[i] : 0;
            int vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }
}

public class PluginManifestJson
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    [JsonPropertyName("minApi")] public string? MinApi { get; set; }
    [JsonPropertyName("forceMinApiVersion")] public string? ForceMinApiVersion { get; set; }
    public EntrypointJson? Entrypoint { get; set; }
}

public class EntrypointJson
{
    public string? Dll { get; set; }
    public string? Type { get; set; }
}
