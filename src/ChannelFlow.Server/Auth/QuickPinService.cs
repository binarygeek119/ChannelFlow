using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FinTv.Auth;

/// <summary>
/// Encrypts M3U/XMLTV URLs with a PIN-derived key and posts ciphertext to the pin server.
/// ChannelFlow never mints pins. The pin server only routes the blob.
/// </summary>
public sealed class QuickPinService
{
    public const string HttpClientName = nameof(QuickPinService);
    public const string PinServerUrl = "https://channelflow.duckdns.org";
    public const int PinLength = 8;
    public const int LifetimeSeconds = 600;

    private const string KeySeedPrefix = "ChannelFlow QuickPin v1";
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    private const int MaxAttemptsPerWindow = 20;

    private readonly IHttpClientFactory _http;
    private readonly ILogger<QuickPinService> _logger;
    private readonly object _rateGate = new();
    private readonly Queue<DateTimeOffset> _attempts = new();

    public QuickPinService(IHttpClientFactory http, ILogger<QuickPinService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<QuickPinDeliverResult> RedeemAsync(string? pin, QuickPinUrls urls, CancellationToken cancellationToken)
    {
        if (!TryRateLimit())
        {
            return QuickPinDeliverResult.Fail(429, "Too many attempts. Wait a minute and try again.");
        }

        var normalized = Normalize(pin);
        if (normalized.Length != PinLength)
        {
            return QuickPinDeliverResult.Fail(400, "Enter the 8-character pin from the app.");
        }

        string ciphertext;
        try
        {
            ciphertext = Encrypt(normalized, urls);
        }
        catch (Exception)
        {
            _logger.LogWarning("Quick pin encrypt failed.");
            return QuickPinDeliverResult.Fail(500, "Could not encrypt connection info.");
        }

        try
        {
            var client = _http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{PinServerUrl}/v1/pins/{normalized}/deliver")
            {
                Content = JsonContent.Create(new { ciphertext }),
            };
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent
                || response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                return QuickPinDeliverResult.Success("Sent. The app should connect in a moment.");
            }

            if ((int)response.StatusCode == 404)
            {
                return QuickPinDeliverResult.Fail(404, "That pin is unknown or expired. Open the app and try the pin it shows now.");
            }

            return QuickPinDeliverResult.Fail(502, "Pin server rejected the request.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning("Quick pin deliver failed.");
            return QuickPinDeliverResult.Fail(502, "Could not reach the pin server.");
        }
    }

    internal static string Normalize(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return string.Empty;
        }

        var chars = pin
            .Where(static c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            .Select(char.ToUpperInvariant)
            .ToArray();
        return new string(chars);
    }

    internal static string Encrypt(string pin, QuickPinUrls urls)
    {
        var plaintext = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["m3u"] = urls.M3u,
            ["xmltv"] = urls.Xmltv,
            ["m3uPublic"] = urls.M3uPublic,
            ["xmltvPublic"] = urls.XmltvPublic,
            ["m3uLocal"] = urls.M3uLocal,
            ["xmltvLocal"] = urls.XmltvLocal,
        });
        var key = SHA256.HashData(Encoding.ASCII.GetBytes(KeySeedPrefix + pin));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, tag.Length);
        gcm.Encrypt(nonce, plainBytes, ciphertext, tag);

        var packed = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, packed, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, packed, nonce.Length + ciphertext.Length, tag.Length);
        return Convert.ToBase64String(packed);
    }

    private bool TryRateLimit()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_rateGate)
        {
            while (_attempts.Count > 0 && now - _attempts.Peek() > RateWindow)
            {
                _attempts.Dequeue();
            }

            if (_attempts.Count >= MaxAttemptsPerWindow)
            {
                return false;
            }

            _attempts.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// URL set delivered to a paired app: primary links plus both public and local variants.
/// </summary>
public sealed record QuickPinUrls(
    string M3u,
    string Xmltv,
    string M3uPublic,
    string XmltvPublic,
    string M3uLocal,
    string XmltvLocal);

public readonly record struct QuickPinDeliverResult(bool Ok, int StatusCode, string Message)
{
    public static QuickPinDeliverResult Success(string message) => new(true, 200, message);

    public static QuickPinDeliverResult Fail(int status, string message) => new(false, status, message);
}
