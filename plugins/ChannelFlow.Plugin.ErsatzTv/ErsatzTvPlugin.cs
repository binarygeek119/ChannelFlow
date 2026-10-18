using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ChannelFlow.PluginSdk;

namespace ChannelFlow.Plugin.ErsatzTv;

public class ErsatzTvConfig
{
    public string BinaryPath { get; set; } = "ersatztv";
    public string PlayoutDir { get; set; } = "";
    public string Url { get; set; } = "http://127.0.0.1:8409";
    public bool AutoStart { get; set; } = false;
    public bool AutoRestart { get; set; } = true;
    public int MaxRestarts { get; set; } = 5;
    public List<string> Args { get; set; } = new();
}

public class ErsatzTvSupervisor
{
    private readonly ErsatzTvConfig _cfg;
    private readonly IPluginLogger _logger;
    private Process? _process;
    private readonly object _lock = new();
    private readonly List<string> _logBuffer = new();
    private const int MaxLogLines = 500;
    private int _restartCount = 0;

    public ErsatzTvSupervisor(ErsatzTvConfig cfg, IPluginLogger logger)
    {
        _cfg = cfg;
        _logger = logger;
        if (string.IsNullOrEmpty(_cfg.PlayoutDir))
            _cfg.PlayoutDir = Path.Combine(Directory.GetCurrentDirectory(), "data", "playout");
    }

    public bool IsRunning => _process is not null && !_process.HasExited;
    public int? Pid => _process?.Id;
    public string Url => _cfg.Url;
    public int RestartCount => _restartCount;
    public IReadOnlyList<string> LogLines => _logBuffer;

    public void AppendLog(string line)
    {
        lock (_logBuffer)
        {
            _logBuffer.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (_logBuffer.Count > MaxLogLines) _logBuffer.RemoveAt(0);
        }
    }

    public async Task<bool> StartAsync()
    {
        lock (_lock)
        {
            if (IsRunning) return true;
            try
            {
                Directory.CreateDirectory(_cfg.PlayoutDir);
                var psi = new ProcessStartInfo
                {
                    FileName = _cfg.BinaryPath,
                    WorkingDirectory = _cfg.PlayoutDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var a in _cfg.Args) psi.ArgumentList.Add(a);
                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += (s, e) => { if (e.Data is not null) { AppendLog("[out] " + e.Data); _logger.Info(e.Data); } };
                _process.ErrorDataReceived += (s, e) => { if (e.Data is not null) { AppendLog("[err] " + e.Data); _logger.Error(e.Data); } };
                _process.Exited += (s, e) =>
                {
                    AppendLog($"[exit] code {_process.ExitCode}");
                    if (_cfg.AutoRestart && _restartCount < _cfg.MaxRestarts)
                    {
                        _restartCount++;
                        AppendLog($"[restart] attempt {_restartCount}/{_cfg.MaxRestarts}");
                        _ = Task.Run(async () => { await Task.Delay(2000); await StartAsync(); });
                    }
                };
                if (!_process.Start()) { AppendLog("[error] Failed to start"); return false; }
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                AppendLog($"[start] pid {_process.Id}, cwd {_cfg.PlayoutDir}");
                return true;
            }
            catch (Exception e)
            {
                AppendLog("[error] " + e.Message);
                _logger.Error(e.Message);
                return false;
            }
        }
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            if (_process is null) return Task.CompletedTask;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch (Exception e) { AppendLog("[error] stop: " + e.Message); }
            finally { _process.Dispose(); _process = null; }
            _restartCount = 0;
            return Task.CompletedTask;
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(500);
        await StartAsync();
    }

    public async Task<bool> HealthAsync()
    {
        if (!IsRunning) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var res = await http.GetAsync(_cfg.Url);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public void ClearLogs() { lock (_logBuffer) _logBuffer.Clear(); }
}

public class ErsatzTvPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.ersatztv", "ErsatzTV", "3.0.0", "3.0.0", Category: "playout");

    private PluginApi? _api;
    private ErsatzTvSupervisor? _sup;
    private ErsatzTvConfig _cfg = new();

    public Task OnLoadAsync(PluginApi api)
    {
        _api = api;
        // Load config from env
        _cfg.BinaryPath = Environment.GetEnvironmentVariable("ERSATZTV_PATH") ?? "/app/ersatztv";
        _cfg.PlayoutDir = Environment.GetEnvironmentVariable("ERSATZTV_PLAYOUT_DIR") ?? Path.Combine(api.Dir, "..", "..", "data", "playout");
        _cfg.Url = Environment.GetEnvironmentVariable("ERSATZTV_URL") ?? "http://127.0.0.1:8409";
        _cfg.AutoStart = Environment.GetEnvironmentVariable("ERSATZTV_AUTOSTART") == "true";
        _cfg.AutoRestart = Environment.GetEnvironmentVariable("ERSATZTV_AUTORESTART") != "false";
        _sup = new ErsatzTvSupervisor(_cfg, api.Logger);
        if (_cfg.AutoStart) _ = _sup.StartAsync();
        return Task.CompletedTask;
    }

    public Task OnEnableAsync() => Task.CompletedTask;
    public Task OnDisableAsync() => Task.CompletedTask;
    public void OnUnload() => _sup?.StopAsync().Wait();
    public Task OnConfigAsync(JsonElement config)
    {
        try
        {
            var c = JsonSerializer.Deserialize<ErsatzTvConfig>(config, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (c is not null) _cfg = c;
        }
        catch { }
        return Task.CompletedTask;
    }

    public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/status", async () => new
        {
            running = _sup!.IsRunning,
            pid = _sup.Pid,
            url = _sup.Url,
            healthy = await _sup.HealthAsync(),
            restartCount = _sup.RestartCount,
            logs = _sup.LogLines
        });
        endpoints.MapPost("/start", async () => new { ok = await _sup!.StartAsync() });
        endpoints.MapPost("/stop", async () => { await _sup!.StopAsync(); return new { ok = true }; });
        endpoints.MapPost("/restart", async () => { await _sup!.RestartAsync(); return new { ok = true }; });
        endpoints.MapGet("/logs", (int tail = 100) =>
        {
            var lines = _sup!.LogLines;
            var t = lines.Count > tail ? lines.Skip(lines.Count - tail).ToList() : lines.ToList();
            return Results.Ok(t);
        });
        endpoints.MapPost("/logs/clear", () => { _sup!.ClearLogs(); return Results.Ok(new { ok = true }); });
    }

    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, _sup?.IsRunning == true ? "running" : "stopped (sidecar not started)");
    public IMediaSource? MediaSource => null;
}
