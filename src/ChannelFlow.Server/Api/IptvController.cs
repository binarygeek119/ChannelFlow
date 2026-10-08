using FinTv.Domain;
using FinTv.Next;
using FinTv.Services;
using FinTv.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

/// <summary>
/// IPTV endpoints for M3U, XMLTV, and live MPEG-TS streams.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("iptv")]
public class IptvController : ControllerBase
{
    private readonly EpgService _epg;
    private readonly StreamService _stream;
    private readonly IPublicBaseUrl _appHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="IptvController"/> class.
    /// </summary>
    /// <param name="epg">EPG service.</param>
    /// <param name="stream">Stream service.</param>
    /// <param name="appHost">Server application host.</param>
    public IptvController(EpgService epg, StreamService stream, IPublicBaseUrl appHost)
    {
        _epg = epg;
        _stream = stream;
        _appHost = appHost;
    }

    /// <summary>
    /// Gets the M3U playlist for all enabled channels.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>M3U playlist content.</returns>
    [HttpGet("channels.m3u")]
    public async Task<IActionResult> GetM3u(CancellationToken cancellationToken)
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost);
        var apiKey = ChannelFlowApiAuth.RequestApiKey(HttpContext) ?? PluginApiKey.Resolve();
        var content = await _epg.GenerateM3uAsync(baseUrl, apiKey, cancellationToken);
        return Content(content, "audio/x-mpegurl");
    }

    /// <summary>
    /// Gets the XMLTV electronic program guide.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XMLTV guide content.</returns>
    [HttpGet("epg.xml")]
    public async Task<IActionResult> GetEpg(CancellationToken cancellationToken)
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost);
        var content = await _epg.GenerateXmlTvBytesAsync(baseUrl, cancellationToken);
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        return File(content, "application/xml; charset=utf-8");
    }

    /// <summary>
    /// Streams the current MPEG-TS output for a channel.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Live transport stream.</returns>
    [HttpHead("stream/{channelId}")]
    [HttpGet("stream/{channelId}")]
    public async Task Stream(string channelId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(channelId, out var id))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!await _stream.ChannelExistsAsync(id, cancellationToken))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // next is the transcoding engine: hand legacy TS bookmarks to its HLS output
        // rather than standing up a second ChannelFlow encode for the same channel.
        var number = NextCoordinatorService.IsEnabled
            ? await _stream.GetChannelNumberAsync(id, cancellationToken)
            : null;
        if (number is not null)
        {
            Response.Redirect($"/iptv/next/channel/{Uri.EscapeDataString(number)}.m3u8");
            return;
        }

        if (HttpMethods.IsHead(Request.Method))
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "video/mp2t";
            return;
        }

        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        await _stream.StreamChannelAsync(id, Response.Body, cancellationToken);
    }

    /// <summary>
    /// Gets the playout item currently airing on a channel.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current playout item.</returns>
    [HttpGet("now/{channelId}")]
    public async Task<IActionResult> GetNow(string channelId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(channelId, out var id))
        {
            return NotFound();
        }

        var current = await _stream.GetCurrentItemAsync(id, cancellationToken);
        return current is null ? NotFound() : Ok(current);
    }

    /// <summary>
    /// Serves a Jellyfin library poster for XMLTV programme icons.
    /// </summary>
    /// <param name="itemId">Jellyfin item identifier (movie, series, or episode).</param>
    /// <param name="guideMetadata">Guide metadata service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Poster image file.</returns>
    [HttpGet("poster/{itemId:guid}")]
    public async Task<IActionResult> GetPoster(
        Guid itemId,
        [FromServices] GuideMetadataService guideMetadata,
        CancellationToken cancellationToken)
    {
        var path = await guideMetadata.GetOrFetchPosterImagePathAsync(itemId, cancellationToken);
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return File(TransparentPng, "image/png");
        }

        return PhysicalFile(path, GetPosterContentType(path));
    }

    private static readonly byte[] TransparentPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    private static string GetPosterContentType(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
    }
}
