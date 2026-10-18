using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChannelFlow.Core;

public interface ILlmChat
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, bool json = false, CancellationToken ct = default);
}

public class LlmConfig
{
    public string Provider { get; set; } = "openai"; // openai | ollama | anthropic
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.7;
}

public class OpenAiCompatibleChat : ILlmChat
{
    private readonly HttpClient _http;
    private readonly LlmConfig _cfg;
    public OpenAiCompatibleChat(HttpClient http, LlmConfig cfg) { _http = http; _cfg = cfg; }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, bool json = false, CancellationToken ct = default)
    {
        var url = _cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
        var body = new
        {
            model = _cfg.Model,
            temperature = _cfg.Temperature,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            response_format = json ? new { type = "json_object" } : null
        };
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(_cfg.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"LLM HTTP {(int)res.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
