using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ChannelFlow.PluginSdk;

namespace ChannelFlow.Plugin.AI;

public class AiProvider
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public string? VisionModel { get; set; }
    public string? TtsModel { get; set; }
    public string? TtsVoice { get; set; }
    public double Temperature { get; set; } = 0.7;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 0;
    public bool Ollama { get; set; } = false;
}

public class AiPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.ai", "AI Provider Suite", "3.0.0", "3.0.0", Category: "ai",
        Permissions: new List<string> { "storage:read", "storage:write", "network:outbound", "api:routes", "ui:pages" });

    private PluginApi? _api;
    private readonly List<AiProvider> _providers = new();
    private readonly object _lock = new();

    public Task OnLoadAsync(PluginApi api)
    {
        _api = api;
        // Load providers from storage
        _ = LoadProvidersAsync();
        return Task.CompletedTask;
    }

    private async Task LoadProvidersAsync()
    {
        try
        {
            var val = await _api!.Storage.GetAsync("providers");
            if (val.HasValue && val.Value.ValueKind == JsonValueKind.Array)
            {
                lock (_lock)
                {
                    _providers.Clear();
                    foreach (var el in val.Value.EnumerateArray())
                    {
                        var p = JsonSerializer.Deserialize<AiProvider>(el.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (p is not null) _providers.Add(p);
                    }
                }
            }
        }
        catch { }
    }

    private async Task SaveProvidersAsync()
    {
        try
        {
            lock (_lock)
            {
                var json = JsonSerializer.SerializeToElement(_providers);
                _ = _api!.Storage.SetAsync("providers", json);
            }
        }
        catch { }
    }

    public Task OnEnableAsync() => Task.CompletedTask;
    public Task OnDisableAsync() => Task.CompletedTask;
    public void OnUnload() { }
    public Task OnConfigAsync(JsonElement config) => Task.CompletedTask;

    public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        // GET / - list providers
        endpoints.MapGet("/", () =>
        {
            lock (_lock)
            {
                var enabled = _providers.Where(p => p.Enabled).OrderBy(p => p.Priority).ToList();
                return Results.Ok(new
                {
                    providers = _providers,
                    chosen = enabled.FirstOrDefault()?.Id
                });
            }
        });

        // POST /providers - create
        endpoints.MapPost("/providers", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                var body = JsonSerializer.Deserialize<AiProvider>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (body is null) return Results.BadRequest(new { error = "invalid body" });
                if (string.IsNullOrEmpty(body.Id)) body.Id = Guid.NewGuid().ToString();
                lock (_lock) { _providers.Add(body); }
                await SaveProvidersAsync();
                _api?.Logger.Info($"ai: created provider '{body.Name}'");
                return Results.Created($"/providers/{body.Id}", body);
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });

        // PUT /providers/{id} - update
        endpoints.MapPut("/providers/{id}", async (string id, HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                var body = JsonSerializer.Deserialize<AiProvider>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (body is null) return Results.BadRequest(new { error = "invalid body" });
                lock (_lock)
                {
                    var idx = _providers.FindIndex(p => p.Id == id);
                    if (idx < 0) return Results.NotFound(new { error = $"no provider with id {id}" });
                    body.Id = id;
                    _providers[idx] = body;
                }
                await SaveProvidersAsync();
                return Results.Ok(body);
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });

        // DELETE /providers/{id}
        endpoints.MapDelete("/providers/{id}", async (string id) =>
        {
            lock (_lock)
            {
                var idx = _providers.FindIndex(p => p.Id == id);
                if (idx < 0) return Results.NotFound(new { error = $"no provider with id {id}" });
                _providers.RemoveAt(idx);
            }
            await SaveProvidersAsync();
            return Results.NoContent();
        });

        // POST /providers/{id}/test - test a saved provider
        endpoints.MapPost("/providers/{id}/test", async (string id, HttpRequest req) =>
        {
            AiProvider? provider;
            lock (_lock) { provider = _providers.FirstOrDefault(p => p.Id == id); }
            if (provider is null) return Results.NotFound(new { error = $"no provider with id {id}" });

            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            JsonElement body = default;
            if (!string.IsNullOrEmpty(json)) body = JsonSerializer.Deserialize<JsonElement>(json);

            var report = await TestProviderAsync(provider, body);
            return Results.Ok(report);
        });

        // POST /test - test a draft provider
        endpoints.MapPost("/test", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                var provider = JsonSerializer.Deserialize<AiProvider>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (provider is null) return Results.BadRequest(new { error = "invalid body" });
                var report = await TestProviderAsync(provider, default);
                return Results.Ok(report);
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });

        // POST /test-all - test all enabled providers in priority order
        endpoints.MapPost("/test-all", async () =>
        {
            List<AiProvider> enabled;
            lock (_lock) { enabled = _providers.Where(p => p.Enabled).OrderBy(p => p.Priority).ToList(); }
            var attempts = new List<object>();
            bool ok = false;
            string? chosen = null;
            foreach (var p in enabled)
            {
                var report = await TestProviderAsync(p, default);
                var probeOk = report.Ok;
                attempts.Add(new { id = p.Id, name = p.Name, ok = probeOk, detail = report.Detail });
                if (probeOk && !ok) { ok = true; chosen = p.Name; }
            }
            return Results.Ok(new { ok, chosen, attempts });
        });

        // POST /models - fetch models from a provider
        endpoints.MapPost("/models", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            JsonElement body = default;
            if (!string.IsNullOrEmpty(json)) body = JsonSerializer.Deserialize<JsonElement>(json);

            var baseUrl = body.TryGetProperty("base_url", out var bu) ? bu.GetString() ?? "" : "";
            var apiKey = body.TryGetProperty("api_key", out var ak) ? ak.GetString() ?? "" : "";
            var ollama = body.TryGetProperty("ollama", out var ol) && ol.GetBoolean();

            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = ollama ? "http://localhost:11434/v1" : "https://api.openai.com/v1";
            baseUrl = baseUrl.Trim().TrimEnd('/');

            try
            {
                using var http = new HttpClient();
                var request = http.GetAsync($"{baseUrl}/models");
                if (!string.IsNullOrWhiteSpace(apiKey) && !ollama)
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                var res = await request;
                if (!res.IsSuccessStatusCode)
                    return Results.BadRequest(new { error = $"provider answered {res.StatusCode}" });

                var text = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(text);
                var models = new List<string>();
                var arr = doc.RootElement.TryGetProperty("data", out var data) ? data : doc.RootElement;
                if (arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var name = item.TryGetProperty("id", out var id) ? id.GetString() : item.GetString();
                        if (!string.IsNullOrEmpty(name)) models.Add(name!);
                    }
                }
                models = models.Distinct().OrderBy(m => m).ToList();
                if (models.Count == 0) return Results.BadRequest(new { error = "provider returned no models" });

                var voices = new[] { "alloy", "echo", "fable", "onyx", "nova", "shimmer", "ash", "ballad", "coral", "sage", "verse" };
                return Results.Ok(new { ok = true, base_url = baseUrl, models, voices });
            }
            catch (Exception e)
            {
                return Results.BadRequest(new { error = $"could not reach the provider: {e.Message}" });
            }
        });
    }

    private async Task<TestReport> TestProviderAsync(AiProvider provider, JsonElement body)
    {
        var baseUrl = provider.BaseUrl.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(provider.ApiKey))
        {
            // Override from body if provided
            if (body.ValueKind == JsonValueKind.Object)
            {
                if (body.TryGetProperty("base_url", out var bu) && !string.IsNullOrEmpty(bu.GetString()))
                    baseUrl = bu.GetString()!.Trim().TrimEnd('/');
                if (body.TryGetProperty("api_key", out var ak) && !string.IsNullOrEmpty(ak.GetString()))
                    provider.ApiKey = ak.GetString()!;
            }
        }

        var model = provider.Model;
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("model", out var m) && !string.IsNullOrEmpty(m.GetString()))
            model = m.GetString()!;

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = await http.PostAsJsonAsync($"{baseUrl}/chat/completions", new
            {
                model = model,
                messages = new[] { new { role = "user", content = "Say OK" } },
                max_tokens = 5
            });
            sw.Stop();
            var ok = res.IsSuccessStatusCode;
            var detail = ok ? $"responded in {sw.ElapsedMilliseconds}ms" : $"HTTP {(int)res.StatusCode}";
            return new TestReport(ok, detail);
        }
        catch (Exception e)
        {
            return new TestReport(false, e.Message);
        }
    }

    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => null;
}

public record TestReport(bool Ok, string Detail);
