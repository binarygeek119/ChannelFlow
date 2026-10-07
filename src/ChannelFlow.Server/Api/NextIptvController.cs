using System.Globalization;
using System.Text.RegularExpressions;
using FinTv.Data;
using FinTv.Domain;
using FinTv.Next;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Api;

/// <summary>
/// ErsatzTV next bridge: the dynamic resolver that tells the engine what to play,
/// the live MPEG-TS source for ChannelFlow-rendered content, and HTTP proxies for
/// next's HLS output so ChannelFlow stays the front door for IPTV clients.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("iptv/next")]
public class NextIptvController : ControllerBase
{
    private readonly StreamService _stream;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NextIptvController> _logger;

    private static readonly Regex SessionUrlRegex = new(
        @"https?://[^/""'\s]*/session/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public NextIptvController(
        StreamService stream,
        IHttpClientFactory httpFactory,
        ILogger<NextIptvController> logger)
    {
        _stream = stream;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// Dynamic resolver endpoint. ErsatzTV next fetches this URI at every item boundary
    /// and plays whatever ChannelFlow returns for the requested wall-clock instant.
    /// Real media becomes a local file slice (next transcodes the file itself); virtual,
    /// commercial, EBS, and off-air content becomes a live MPEG-TS HTTP source served by
    /// ChannelFlow's own engines.
    /// </summary>
    [HttpGet("resolve/{channelId}")]
    public async Task<IActionResult> Resolve(string channelId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(channelId, out var id) || !IsValidInternalToken())
        {
            return Unauthorized();
        }

        if (!NextCoordinatorService.IsEnabled)
        {
            return NotFound();
        }

        var now = TryParseInstantHeader("x-etv-now") ?? DateTimeOffset.UtcNow;
        var until = TryParseInstantHeader("x-etv-until") ?? now.AddDays(1);
        var atUtc = now.UtcDateTime;

        using var scope = HttpContext.RequestServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.Enabled, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        var item = channel.IsContinuousLive
            ? (PlayoutItem?)null
            : await _stream.GetItemAtAsync(id, atUtc, cancellationToken);

        var resolved = channel.IsContinuousLive
            ? BuildLiveItem(channel, now, until)
            : await BuildResolvedItemAsync(channel, item, now, until, cancellationToken);

        return Content(FinTvJson.Serialize(resolved), "application/json");
    }

    /// <summary>
    /// Whole-window item for a continuous-live channel. A configured RTSP feed is handed
    /// to next as an <c>rtsp</c> source (next pulls and transcodes it); otherwise the
    /// channel's own compositor is surfaced as a live MPEG-TS HTTP source.
    /// </summary>
    private NextPlayoutItem BuildLiveItem(Channel channel, DateTimeOffset now, DateTimeOffset until)
    {
        if (!string.IsNullOrWhiteSpace(channel.RtspUrl))
        {
            return BuildRtspItem("live", now, until, channel.RtspUrl.Trim());
        }

        return BuildHttpItem("live", now, until, BuildSourceUri(channel.Id, itemId: null));
    }

    private async Task<NextPlayoutItem> BuildResolvedItemAsync(
        Channel channel,
        PlayoutItem? item,
        DateTimeOffset now,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        if (item is not null
            && !item.IsVirtual
            && item.CommercialId is null
            && item.JellyfinItemId.HasValue)
        {
            // Real media: hand the file to next with exact in/out points so next does
            // the encode (hardware accel) without ChannelFlow touching ffmpeg.
            using var scope = HttpContext.RequestServices.CreateScope();
            var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
            var catalog = scope.ServiceProvider.GetRequiredService<JellyfinCatalogService>();
            var holidays = scope.ServiceProvider.GetRequiredService<HolidayChannelService>();

            var mediaItem = libraryManager.GetItemById(item.JellyfinItemId.Value);
            var path = mediaItem is null ? null : catalog.GetMediaPath(mediaItem);
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
            {
                var elapsed = now.UtcDateTime - item.Start;
                if (elapsed < TimeSpan.Zero)
                {
                    elapsed = TimeSpan.Zero;
                }

                var inPoint = item.InPoint + elapsed;
                var outPoint = item.OutPoint;
                if (outPoint - inPoint >= TimeSpan.FromMilliseconds(500))
                {
                    var finish = item.Finish > now.UtcDateTime
                        ? item.Finish
                        : now.UtcDateTime + TimeSpan.FromSeconds(1);
                    if (finish > until.UtcDateTime)
                    {
                        finish = until.UtcDateTime;
                    }

                    var bugPath = ResolveNextBugPath(channel, item.Start, holidays);

                    return new NextPlayoutItem
                    {
                        Id = item.Id.ToString("N"),
                        Start = now.ToString("o", CultureInfo.InvariantCulture),
                        Finish = finish.ToString("o", CultureInfo.InvariantCulture),
                        Source = new NextLocalSource
                        {
                            Path = path,
                            InPointMs = (long)Math.Round(inPoint.TotalMilliseconds),
                            OutPointMs = (long)Math.Round(outPoint.TotalMilliseconds),
                        },
                        Graphics = BuildBugGraphics(bugPath, channel.BugPlacement),
                    };
                }
            }
        }

        // Virtual/commercial/EBS/off-air content: ChannelFlow's existing renderers produce
        // a live MPEG-TS stream that next normalizes and segments. A specific item is
        // streamed from its own start so work-ahead doesn't leak the previous item's tail.
        var finishUtc = until.UtcDateTime;
        if (item is not null && item.Finish > now.UtcDateTime && item.Finish < finishUtc)
        {
            finishUtc = item.Finish;
        }
        else if (item is null)
        {
            var nextStart = await _stream.GetNextItemStartAsync(channel.Id, now.UtcDateTime, cancellationToken);
            if (nextStart.HasValue && nextStart.Value > now.UtcDateTime && nextStart.Value < finishUtc)
            {
                finishUtc = nextStart.Value;
            }
        }

        return new NextPlayoutItem
        {
            Id = item?.Id.ToString("N") ?? "offair",
            Start = now.ToString("o", CultureInfo.InvariantCulture),
            Finish = finishUtc.ToString("o", CultureInfo.InvariantCulture),
            Source = new NextHttpSource
            {
                Uri = BuildSourceUri(channel.Id, item?.Id),
                IsLive = true,
            },
        };
    }

    /// <summary>
    /// Live MPEG-TS source consumed by next for ChannelFlow-rendered content (weather star,
    /// news, art slides, bumpers, bundled videos, YouTube commercials, EBS). Resolves the
    /// item via <c>itemId</c> when given, else streams whatever is current on the channel.
    /// </summary>
    [HttpGet("source/{channelId}")]
    public async Task Source(string channelId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(channelId, out var id) || !IsValidInternalToken())
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!await _stream.ChannelExistsAsync(id, cancellationToken))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
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

        if (Guid.TryParse(Request.Query["itemId"], out var itemId))
        {
            await _stream.StreamNextItemAsync(id, itemId, Response.Body, cancellationToken);
        }
        else
        {
            await _stream.StreamChannelAsync(id, Response.Body, cancellationToken);
        }
    }

    /// <summary>
    /// Proxies a channel's HLS master playlist from next, rewriting the absolute
    /// <c>/session/...</c> media URLs it contains to point back at ChannelFlow.
    /// </summary>
    [HttpGet("channel/{number}.m3u8")]
    public async Task ChannelPlaylist(string number, CancellationToken cancellationToken)
    {
        if (!NextCoordinatorService.IsEnabled)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var client = _httpFactory.CreateClient("next");
        try
        {
            using var response = await client.GetAsync(
                NextUrl($"/channel/{number}.m3u8"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Response.StatusCode = (int)response.StatusCode;
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var rewritten = SessionUrlRegex.Replace(
                body,
                $"{ReverseProxyHosting.PublicOrigin(Request)}/iptv/next/session/");

            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = response.Content.Headers.ContentType?.ToString()
                ?? "application/vnd.apple.mpegurl";
            await Response.WriteAsync(rewritten, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Next HLS proxy failed for channel {Number}", number);
            if (!Response.HasStarted)
            {
                Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    /// <summary>
    /// Proxies HLS media playlists and segments (live.m3u8, live000000.ts, subtitles)
    /// from next's session output folder.
    /// </summary>
    [HttpGet("session/{number}/{**file}")]
    public async Task SessionFiles(string number, string file, CancellationToken cancellationToken)
    {
        if (!NextCoordinatorService.IsEnabled)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var client = _httpFactory.CreateClient("next");
        try
        {
            using var response = await client.GetAsync(
                NextUrl($"/session/{number}/{file}"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Response.StatusCode = (int)response.StatusCode;
                return;
            }

            Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
            {
                Response.ContentType = contentType.ToString();
            }

            await using var upstream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await upstream.CopyToAsync(Response.Body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Next session proxy failed for {Path}", "/session/" + number + "/" + file);
            if (!Response.HasStarted)
            {
                Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    private static NextPlayoutItem BuildHttpItem(
        string id,
        DateTimeOffset start,
        DateTimeOffset finish,
        string uri)
        => new()
        {
            Id = id,
            Start = start.ToString("o", CultureInfo.InvariantCulture),
            Finish = finish.ToString("o", CultureInfo.InvariantCulture),
            Source = new NextHttpSource { Uri = uri, IsLive = true },
        };

    private static NextPlayoutItem BuildRtspItem(
        string id,
        DateTimeOffset start,
        DateTimeOffset finish,
        string uri)
        => new()
        {
            Id = id,
            Start = start.ToString("o", CultureInfo.InvariantCulture),
            Finish = finish.ToString("o", CultureInfo.InvariantCulture),
            Source = new NextRtspSource { Uri = uri },
        };

    private string BuildSourceUri(Guid channelId, Guid? itemId)
    {
        var next = NextCoordinatorService.Configured!;
        var uri = $"{next.ResolverBaseUrl!.TrimEnd('/')}/iptv/next/source/{channelId:N}" +
            $"?token={Uri.EscapeDataString(next.ResolverToken!)}";
        if (itemId.HasValue)
        {
            uri += $"&itemId={itemId.Value:N}";
        }

        return uri;
    }

    private string NextUrl(string path)
    {
        var next = NextCoordinatorService.Configured!;
        return $"{next.BaseUrl!.TrimEnd('/')}{path}";
    }

    private bool IsValidInternalToken()
    {
        var next = NextCoordinatorService.Configured;
        if (next is null || string.IsNullOrWhiteSpace(next.ResolverToken))
        {
            return false;
        }

        var provided = Request.Query["token"].ToString();
        return !string.IsNullOrWhiteSpace(provided)
            && string.Equals(provided, next.ResolverToken, StringComparison.Ordinal);
    }

    private DateTimeOffset? TryParseInstantHeader(string name)
    {
        var value = Request.Headers[name].ToString();
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string? ResolveNextBugPath(Channel channel, DateTime scheduleUtc, HolidayChannelService holidays)
    {
        if (channel.BugPlacement == BugPlacementMode.None)
        {
            return null;
        }

        if (holidays.IsHolidayChannel(channel))
        {
            var date = holidays.GetScheduleDateUtc(scheduleUtc);
            return holidays.ResolveEffectiveLogoPath(channel, date);
        }

        return channel.ChannelLogoPath;
    }

    private static List<NextGraphicsLayer>? BuildBugGraphics(string? bugPath, BugPlacementMode placement)
    {
        if (string.IsNullOrWhiteSpace(bugPath) || !System.IO.File.Exists(bugPath) || placement == BugPlacementMode.None)
        {
            return null;
        }

        var location = placement switch
        {
            BugPlacementMode.TopLeft => "top_left",
            BugPlacementMode.TopRight => "top_right",
            BugPlacementMode.BottomLeft => "bottom_left",
            BugPlacementMode.BottomRight => "bottom_right",
            _ => "bottom_right",
        };

        return
        [
            new NextGraphicsLayer
            {
                Source = new NextLocalSource { Path = bugPath },
                Location = location,
                WidthPercent = 10,
                HorizontalMarginPercent = 2,
                VerticalMarginPercent = 2,
                OpacityPercent = 100,
                WithinSourceContent = true
            },
        ];
    }
}