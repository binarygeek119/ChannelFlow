using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChannelFlow.Database;

namespace ChannelFlow.Core;

public class QuickPinService
{
    private const string KeyContext = "ChannelFlow QuickPin v1";
    private const string DefaultServer = "https://channelflow.duckdns.org";

    public static string NormalizePin(string pin)
    {
        return new string(pin.Where(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')).Select(char.ToUpperInvariant).ToArray());
    }

    public static bool IsValidPin(string pin) => NormalizePin(pin).Length == 8;

    private static byte[] DeriveKey(string pin)
    {
        using var sha = SHA256.Create();
        var ctx = Encoding.ASCII.GetBytes(KeyContext);
        var pinBytes = Encoding.ASCII.GetBytes(NormalizePin(pin));
        var input = new byte[ctx.Length + pinBytes.Length];
        Buffer.BlockCopy(ctx, 0, input, 0, ctx.Length);
        Buffer.BlockCopy(pinBytes, 0, input, ctx.Length, pinBytes.Length);
        return sha.ComputeHash(input);
    }

    public static string Encrypt(string pin, byte[] plaintext)
    {
        var key = DeriveKey(pin);
        using var aes = new AesGcm(key, 16);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var blob = new byte[12 + ciphertext.Length + 16];
        Buffer.BlockCopy(nonce, 0, blob, 0, 12);
        Buffer.BlockCopy(ciphertext, 0, blob, 12, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, blob, 12 + ciphertext.Length, 16);
        return Convert.ToBase64String(blob);
    }

    public static string AppendKey(string url, string apiKey)
    {
        apiKey = apiKey.Trim();
        if (string.IsNullOrEmpty(apiKey)) return url;
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}apiKey={apiKey}";
    }

    public static object BuildPayload(GeneralSettings settings, bool primaryLocal, string apiKey)
    {
        string? BaseUrl(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().TrimEnd('/');
        var local = BaseUrl(settings.LocalUrl);
        var pub = BaseUrl(settings.PublicUrl);

        (string m3u, string xmltv)? LocalUrls() => local is null ? null : (AppendKey($"{local}/live/channels.m3u", apiKey), AppendKey($"{local}/live/xmltv.xml", apiKey));
        (string m3u, string xmltv)? PublicUrls() => pub is null ? null : (AppendKey($"{pub}/live/channels.m3u", apiKey), AppendKey($"{pub}/live/xmltv.xml", apiKey));

        var localUrls = LocalUrls();
        var publicUrls = PublicUrls();

        string? localM3u = localUrls?.m3u, localXmltv = localUrls?.xmltv;
        string? publicM3u = publicUrls?.m3u, publicXmltv = publicUrls?.xmltv;

        string m3u, xmltv;
        if (primaryLocal)
        {
            m3u = localM3u ?? publicM3u ?? "";
            xmltv = localXmltv ?? publicXmltv ?? "";
        }
        else
        {
            m3u = publicM3u ?? localM3u ?? "";
            xmltv = publicXmltv ?? localXmltv ?? "";
        }

        return new
        {
            m3u,
            xmltv,
            m3uPublic = publicM3u ?? "",
            xmltvPublic = publicXmltv ?? "",
            m3uLocal = localM3u ?? "",
            xmltvLocal = localXmltv ?? ""
        };
    }

    public static bool PrimaryIsLocal(GeneralSettings settings, string? requestHost)
    {
        if (string.IsNullOrEmpty(requestHost)) return true;
        var host = requestHost.Split("://").LastOrDefault()?.Split('/')[0]?.Split(':')[0]?.ToLowerInvariant() ?? "";
        if (host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0") return true;
        // check if request host matches local url host
        if (!string.IsNullOrEmpty(settings.LocalUrl))
        {
            var localHost = settings.LocalUrl.Split("://").LastOrDefault()?.Split('/')[0]?.Split(':')[0]?.ToLowerInvariant() ?? "";
            if (localHost == host) return true;
        }
        return false;
    }

    public static async Task<bool> DeliverAsync(HttpClient http, string server, string pin, string ciphertext)
    {
        var origin = server.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(origin)) throw new Exception("no pin server is configured");
        var normalizedPin = NormalizePin(pin);
        var url = $"{origin}/v1/pins/{normalizedPin}/deliver";
        var response = await http.PostAsJsonAsync(url, new { ciphertext });
        return response.StatusCode == System.Net.HttpStatusCode.NoContent;
    }

    public static string ResolvePinServer() => DefaultServer;
}
