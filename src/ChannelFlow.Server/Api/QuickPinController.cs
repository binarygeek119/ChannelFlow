using FinTv.Auth;
using FinTv.Domain;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

/// <summary>
/// Delivers encrypted M3U/XMLTV URLs to a waiting app through the pin server.
/// </summary>
[ApiController]
[Route("api/quick-pin")]
[Authorize(Policy = "admin")]
public class QuickPinController : ControllerBase
{
    private readonly QuickPinService _quickPins;
    private readonly IPublicBaseUrl _appHost;
    private readonly PairedTvClientStore _clients;

    public QuickPinController(QuickPinService quickPins, IPublicBaseUrl appHost, PairedTvClientStore clients)
    {
        _quickPins = quickPins;
        _appHost = appHost;
        _clients = clients;
    }

    /// <summary>
    /// Encrypts Live TV URLs with the typed PIN and posts ciphertext to the pin server.
    /// </summary>
    [HttpPost("redeem")]
    public async Task<ActionResult> Redeem([FromBody] QuickPinRedeemRequest? request, CancellationToken cancellationToken)
    {
        var publicBaseUrl = EpgService.GetPublicBaseUrl(Request, _appHost);
        var requestOrigin = ReverseProxyHosting.PublicOrigin(Request);
        var localBaseUrl = ReverseProxyHosting.LocalBaseUrl(Request);
        var client = _clients.Issue();
        var (m3uPublic, xmltvPublic) = PluginApiKey.BuildLiveTvUrls(publicBaseUrl, client.ApiKey);
        var (m3uLocal, xmltvLocal) = PluginApiKey.BuildLiveTvUrls(localBaseUrl, client.ApiKey);

        // Pairing from a local address (LAN IP or loopback) means the admin and the app being
        // paired are on the same network: send the local links as primary. Both sets always go
        // out so the app can fall back either way.
        var onLocalNetwork = ReverseProxyHosting.IsLocalOrigin(requestOrigin)
            || string.Equals(requestOrigin.TrimEnd('/'), localBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        var (m3u, xmltv) = onLocalNetwork ? (m3uLocal, xmltvLocal) : (m3uPublic, xmltvPublic);

        var urls = new QuickPinUrls(m3u, xmltv, m3uPublic, xmltvPublic, m3uLocal, xmltvLocal);
        var result = await _quickPins.RedeemAsync(request?.Pin, urls, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            _clients.Remove(client.Id);
        }

        return StatusCode(result.StatusCode, new { ok = result.Ok, message = result.Message });
    }
}

public class QuickPinRedeemRequest
{
    public string? Pin { get; set; }
}
