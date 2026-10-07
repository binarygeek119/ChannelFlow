using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FinTv.Configuration;
using FinTv.Domain;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public class LlmClientService
{
    private static readonly JsonSerializerOptions RequestJsonOptions = CreateRequestJsonOptions();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmClientService> _logger;

    public LlmClientService(IHttpClientFactory httpClientFactory, ILogger<LlmClientService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> CompleteJsonAsync(
        AiProvider provider,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default,
        string? openAiApiKeyOverride = null,
        string? veniceApiKeyOverride = null)
    {
        var settings = FinTvRuntime.Current?.Configuration.Ai ?? new AiSettings();
        var (apiKey, model, baseUrl) = ResolveProvider(provider, settings, openAiApiKeyOverride, veniceApiKeyOverride);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(BuildMissingApiKeyMessage(provider, settings, openAiApiKeyOverride, veniceApiKeyOverride));
        }

        var useJsonResponseFormat = provider != AiProvider.Venice;
        var effectiveSystemPrompt = useJsonResponseFormat
            ? systemPrompt
            : systemPrompt + "\nReply with raw JSON only. No markdown fences or commentary.";

        var payload = useJsonResponseFormat
            ? (object)new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = effectiveSystemPrompt },
                    new { role = "user", content = userPrompt }
                },
                response_format = new { type = "json_object" },
                temperature = 0.4
            }
            : new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = effectiveSystemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.4
            };

        using var request = CreateAuthorizedRequest(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, RequestJsonOptions), Encoding.UTF8, "application/json");

        var body = await SendAsync(request, cancellationToken);
        var content = ExtractChatContent(body);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("LLM returned an empty response.");
        }

        FinTvDebugLog.Ai(
            _logger,
            "LLM completed via {Provider}/{Model}: response={ResponseChars} chars, jsonMode={JsonMode}",
            provider,
            model,
            content.Length,
            useJsonResponseFormat);

        return useJsonResponseFormat ? content : ExtractJsonFromText(content);
    }

    public async Task<byte[]> SynthesizeSpeechAsync(
        string text,
        string voice,
        CancellationToken cancellationToken = default)
    {
        var settings = FinTvRuntime.Current?.Configuration.Ai ?? new AiSettings();
        var provider = settings.DefaultProvider;
        var (apiKey, _, baseUrl) = ResolveProvider(provider, settings);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(BuildMissingApiKeyMessage(provider, settings, null, null));
        }

        var input = text.Trim();
        if (input.Length == 0)
        {
            throw new InvalidOperationException("TTS text is empty.");
        }

        var model = ResolveSpeechModel(provider, settings);
        var speechVoice = ResolveSpeechVoice(provider, settings, voice);
        var payload = new
        {
            model,
            input,
            voice = speechVoice,
            response_format = "mp3"
        };

        using var request = CreateAuthorizedRequest(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/audio/speech", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, RequestJsonOptions), Encoding.UTF8, "application/json");
        var client = _httpClientFactory.CreateClient(nameof(LlmClientService));
        using var response = await client.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = Encoding.UTF8.GetString(bytes);
            _logger.LogWarning(
                "AI TTS failed ({Status}) via {Provider}/{Model} voice={Voice}: {Body}",
                (int)response.StatusCode,
                provider,
                model,
                speechVoice,
                body);
            throw new InvalidOperationException(FormatSpeechError((int)response.StatusCode, provider, model, body));
        }

        if (bytes.Length < 64)
        {
            throw new InvalidOperationException("AI TTS returned an empty clip.");
        }

        return bytes;
    }

    private static string ResolveSpeechModel(AiProvider provider, AiSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.TtsModel))
        {
            return settings.TtsModel.Trim();
        }

        return provider == AiProvider.Venice ? "tts-kokoro" : "tts-1";
    }

    private static string ResolveSpeechVoice(AiProvider provider, AiSettings settings, string? requested)
    {
        var voice = !string.IsNullOrWhiteSpace(settings.TtsVoice)
            ? settings.TtsVoice.Trim()
            : MapSpeechVoice(requested);

        return provider == AiProvider.Venice ? MapVeniceSpeechVoice(voice) : voice;
    }

    private static string MapSpeechVoice(string? voice)
    {
        var key = (voice ?? "").Trim().ToLowerInvariant();
        return key switch
        {
            "nova" or "catherine" or "catherine wolfe" => "nova",
            "en-gb" or "en-uk" => "fable",
            "es" or "es-es" or "es-mx" => "nova",
            "fr" or "fr-fr" => "shimmer",
            "de" or "de-de" => "onyx",
            "it" or "it-it" => "echo",
            "pt" or "pt-br" => "nova",
            "ja" or "ko" or "zh-cn" or "zh" => "alloy",
            _ => "alloy"
        };
    }

    private static string MapVeniceSpeechVoice(string voice)
    {
        if (voice.Contains('_', StringComparison.Ordinal))
        {
            return voice;
        }

        return voice.Trim().ToLowerInvariant() switch
        {
            "alloy" => "af_alloy",
            "echo" => "am_echo",
            "fable" => "bm_fable",
            "onyx" => "am_onyx",
            "nova" => "af_nova",
            "shimmer" => "af_bella",
            "coral" => "af_sarah",
            "sage" => "af_kore",
            "ash" => "am_michael",
            "catherine" or "catherine wolfe" => "af_sky",
            _ => voice
        };
    }

    private static string FormatSpeechError(int statusCode, AiProvider provider, string model, string body)
    {
        var providerMessage = TryExtractProviderError(body);
        if (!string.IsNullOrWhiteSpace(providerMessage))
        {
            return providerMessage;
        }

        var hint = provider == AiProvider.Venice
            ? " Venice TTS expects a model such as tts-kokoro and a matching voice (for example af_sky)."
            : string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            return $"AI TTS failed ({statusCode}) via {provider}/{model}.{hint}";
        }

        var excerpt = body.Length <= 300 ? body : body[..300] + "…";
        return $"AI TTS failed ({statusCode}) via {provider}/{model}: {excerpt}";
    }

    internal static string ExtractJsonFromText(string content)
    {
        content = content.Trim();

        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            var fenceEnd = content.IndexOf('\n');
            if (fenceEnd >= 0)
            {
                content = content[(fenceEnd + 1)..].TrimEnd();
            }

            if (content.EndsWith("```", StringComparison.Ordinal))
            {
                content = content[..^3].TrimEnd();
            }
        }

        var start = content.IndexOf('{');
        if (start < 0)
        {
            return content;
        }

        var depth = 0;
        for (var i = start; i < content.Length; i++)
        {
            if (content[i] == '{')
            {
                depth++;
            }
            else if (content[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return content[start..(i + 1)];
                }
            }
        }

        return content[start..];
    }

    public async Task TestConnectionAsync(
        AiProvider provider,
        string? openAiApiKeyOverride = null,
        string? veniceApiKeyOverride = null,
        CancellationToken cancellationToken = default)
    {
        var settings = FinTvRuntime.Current?.Configuration.Ai ?? new AiSettings();
        var (apiKey, model, baseUrl) = ResolveProvider(provider, settings, openAiApiKeyOverride, veniceApiKeyOverride);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(BuildMissingApiKeyMessage(provider, settings, openAiApiKeyOverride, veniceApiKeyOverride));
        }

        if (provider == AiProvider.OpenAi)
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models", apiKey);
            await SendAsync(request, cancellationToken);
            return;
        }

        var content = await CompleteJsonAsync(
            provider,
            "You are a connectivity test. Reply with JSON {\"ok\":true}.",
            "Reply now.",
            cancellationToken,
            openAiApiKeyOverride,
            veniceApiKeyOverride);

        if (!content.Contains("ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected test response from LLM provider.");
        }
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(LlmClientService));

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await client.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("LLM request failed ({Status}): {Body}", (int)response.StatusCode, body);
                    var providerMessage = TryExtractProviderError(body);
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(providerMessage)
                            ? $"LLM request failed ({(int)response.StatusCode})."
                            : providerMessage);
                }

                return body;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == 0)
                {
                    _logger.LogWarning(
                        "LLM request timed out after {Seconds} seconds; retrying once.",
                        (int)client.Timeout.TotalSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    request = await CloneRequestAsync(request, cancellationToken);
                    continue;
                }

                throw new InvalidOperationException(
                    $"LLM request timed out after {(int)client.Timeout.TotalSeconds} seconds. Try again or use a faster model.",
                    ex);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    "LLM request could not be sent. Check that the API key contains only plain text characters and does not include a 'Bearer ' prefix.",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    "Could not reach the LLM API from the Jellyfin server. Check internet access, DNS, and firewall rules.",
                    ex);
            }
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url, string apiKey)
    {
        var normalizedKey = NormalizeApiKey(apiKey);
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            throw new InvalidOperationException("API key is not configured.");
        }

        try
        {
            var request = new HttpRequestMessage(method, url)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalizedKey);
            return request;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "API key format is invalid. Paste only the key itself (for example sk-...), without quotes or a Bearer prefix.",
                ex);
        }
    }

    private static string NormalizeApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return string.Empty;
        }

        var trimmed = apiKey.Trim().Trim('"', '\'');
        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].Trim();
        }

        return trimmed;
    }

    private static string? TryExtractProviderError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Fall back to generic status message.
        }

        return null;
    }

    private static string ExtractChatContent(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var firstChoice = choices[0];
            if (!firstChoice.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return content.GetString() ?? string.Empty;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Could not parse LLM response JSON: {ex.Message}");
        }
    }

    private static JsonSerializerOptions CreateRequestJsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

    private static string BuildMissingApiKeyMessage(
        AiProvider provider,
        AiSettings settings,
        string? openAiApiKeyOverride,
        string? veniceApiKeyOverride)
    {
        if (provider == AiProvider.Venice)
        {
            return !string.IsNullOrWhiteSpace(veniceApiKeyOverride) || !string.IsNullOrWhiteSpace(settings.VeniceApiKey)
                ? "Venice API key is empty after trimming. Re-paste the key without quotes or a Bearer prefix."
                : "No Venice API key is saved. Enter your Venice API key in the field above, then click Test Connection.";
        }

        return !string.IsNullOrWhiteSpace(openAiApiKeyOverride) || !string.IsNullOrWhiteSpace(settings.OpenAiApiKey)
            ? "OpenAI API key is empty after trimming. Re-paste the key without quotes or a Bearer prefix."
            : "No OpenAI API key is saved. Enter your sk-... key in the OpenAI API key field, then click Test Connection.";
    }

    private static (string ApiKey, string Model, string BaseUrl) ResolveProvider(
        AiProvider provider,
        AiSettings settings,
        string? openAiApiKeyOverride = null,
        string? veniceApiKeyOverride = null)
    {
        return provider switch
        {
            AiProvider.Venice => (
                NormalizeApiKey(!string.IsNullOrWhiteSpace(veniceApiKeyOverride) ? veniceApiKeyOverride : settings.VeniceApiKey),
                string.IsNullOrWhiteSpace(settings.VeniceModel) ? "gpt-4o-mini" : settings.VeniceModel.Trim(),
                "https://api.venice.ai/api/v1"),
            _ => (
                NormalizeApiKey(!string.IsNullOrWhiteSpace(openAiApiKeyOverride) ? openAiApiKeyOverride : settings.OpenAiApiKey),
                string.IsNullOrWhiteSpace(settings.OpenAiModel) ? "gpt-4o-mini" : settings.OpenAiModel.Trim(),
                "https://api.openai.com/v1")
        };
    }
}
