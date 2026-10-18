using System.Text.Json;

namespace ChannelFlow.Core;

public class ClientLogsService
{
    private readonly string _configDir;
    public ClientLogsService(string configDir) => _configDir = configDir;

    private string Root => Path.Combine(_configDir, "clients");

    public List<string> ListDevices()
    {
        if (!Directory.Exists(Root)) return new();
        return Directory.GetDirectories(Root).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().OrderBy(n => n).ToList();
    }

    public List<string> ListFiles(string deviceId)
    {
        var dir = Path.Combine(Root, deviceId);
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "channelflow-client-*.log").Select(Path.GetFileName).Where(n => n is not null).Cast<string>().OrderByDescending(n => n).ToList();
    }

    public string? ReadFile(string deviceId, string fileName)
    {
        // sanitize
        if (fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\")) return null;
        var path = Path.Combine(Root, deviceId, fileName);
        if (!File.Exists(path)) return null;
        return File.ReadAllText(path);
    }

    public void Ingest(string? deviceId, string? deviceName, string? appVersion, string? osVersion, List<ClientLogEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        // sanitize device id
        var cleaned = new string(deviceId.Trim().Where(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '-').ToArray());
        if (string.IsNullOrEmpty(cleaned)) return;
        if (cleaned.Length > 64) cleaned = cleaned[..64];

        var dir = Path.Combine(Root, cleaned);
        Directory.CreateDirectory(dir);

        // write device metadata
        var metaPath = Path.Combine(dir, "device.json");
        var meta = new { deviceId = cleaned, deviceName, appVersion, osVersion, lastSeen = DateTime.UtcNow };
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));

        // append to dated file
        var day = DateTime.UtcNow.ToString("yyyyMMdd");
        var filePath = Path.Combine(dir, $"channelflow-client-{day}.log");
        using var writer = new StreamWriter(filePath, append: true);
        foreach (var entry in entries.Take(200))
        {
            var ts = entry.Timestamp ?? DateTime.UtcNow.ToString("o");
            var level = entry.Level ?? "info";
            var tag = entry.Tag ?? "";
            var msg = entry.Message ?? "";
            if (msg.Length > 8000) msg = msg[..8000];
            writer.WriteLine($"[{ts}] [{level}] [{tag}] {msg}");
            if (!string.IsNullOrEmpty(entry.Exception))
            {
                var ex = entry.Exception.Length > 16000 ? entry.Exception[..16000] : entry.Exception;
                writer.WriteLine($"  EXCEPTION: {ex}");
            }
        }
    }
}

public class ClientLogEntry
{
    public string? Timestamp { get; set; }
    public string? Level { get; set; }
    public string? Tag { get; set; }
    public string? Message { get; set; }
    public string? Exception { get; set; }
}

public class ClientLogIngestRequest
{
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? AppVersion { get; set; }
    public string? OsVersion { get; set; }
    public List<ClientLogEntry> Entries { get; set; } = new();
}
