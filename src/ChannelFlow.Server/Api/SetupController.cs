using ChannelFlow.CommercialDetect;
using FinTv;
using FinTv.Auth;
using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using FinTv.News;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Api;

/// <summary>
/// Setup helper endpoints for Jellyfin Live TV integration.
/// </summary>
[ApiController]
[Route("api/setup")]
public class SetupController : ControllerBase
{
    private readonly IPublicBaseUrl _appHost;
    private readonly JellyfinCatalogService _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="SetupController"/> class.
    /// </summary>
    public SetupController(IPublicBaseUrl appHost, JellyfinCatalogService catalog)
    {
        _appHost = appHost;
        _catalog = catalog;
    }

    /// <summary>
    /// Gets M3U and XMLTV URLs for Jellyfin Live TV configuration.
    /// </summary>
    /// <returns>Setup URLs and instructions.</returns>
    [HttpGet("urls")]
    [AllowAnonymous]
    public ActionResult<object> GetUrls()
    {
        try
        {
            return Ok(BuildUrlResponse());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not build setup URLs: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets ChannelFlow setup settings for the admin UI.
    /// </summary>
    /// <returns>Setup settings.</returns>
    [HttpGet("settings")]
    [Authorize(Policy = "admin")]
    public ActionResult<object> GetSettings()
    {
        return Ok(new
        {
            publicBaseUrl = FinTvRuntime.Current?.Configuration.PublicBaseUrl ?? string.Empty,
            ebsBackgroundMusicSource = (int)(FinTvRuntime.Current?.Configuration.EbsBackgroundMusicSource ?? EbsBackgroundMusicSource.LocalPacks),
            ebsBackgroundMusicLibraryName = FinTvRuntime.Current?.Configuration.EbsBackgroundMusicLibraryName ?? "Background Music",
            ebsBackgroundMusicLibraryId = FinTvRuntime.Current?.Configuration.EbsBackgroundMusicLibraryId ?? string.Empty,
            weatherStarBaseUrl = FinTvRuntime.Current?.Configuration.WeatherStarBaseUrl ?? WeatherStarChannelService.DefaultWeatherStarBaseUrl,
            weatherStarPermalinkQuery = FinTvRuntime.Current?.Configuration.WeatherStarPermalinkQuery
                ?? WeatherStarChannelService.DefaultWeatherStarPermalinkQuery,
            autoStartPlaywrightDockerSidecar = FinTvRuntime.Current?.Configuration.AutoStartPlaywrightDockerSidecar ?? false,
            autoStartWeatherStarDocker = FinTvRuntime.Current?.Configuration.AutoStartWeatherStarDocker ?? false,
            weatherStarAutoWideForSixteenNine = FinTvRuntime.Current?.Configuration.WeatherStarAutoWideForSixteenNine ?? true,
            playoutDaysToBuild = PlayoutScheduleHelper.GetPlayoutDaysToBuild(),
            ws4kpHostPort = FinTvRuntime.Current?.Configuration.Ws4kp.HostPort ?? 8080,
            ws4kpImage = FinTvRuntime.Current?.Configuration.Ws4kp.Image ?? "ghcr.io/netbymatt/ws4kp",
            ws3kpHostPort = FinTvRuntime.Current?.Configuration.Ws3kp.HostPort ?? 8083,
            ws3kpImage = FinTvRuntime.Current?.Configuration.Ws3kp.Image ?? "ghcr.io/netbymatt/ws3kp",
            musicLibraries = _catalog.GetMusicLibraries().Select(l => new { id = l.Id, name = l.Name })
        });
    }

    /// <summary>
    /// Updates ChannelFlow setup settings and returns refreshed Live TV URLs.
    /// </summary>
    /// <param name="request">Setup settings.</param>
    /// <returns>Updated setup URLs.</returns>
    [HttpPut("settings")]
    [Authorize(Policy = "admin")]
    public ActionResult<object> UpdateSettings([FromBody] SetupSettingsRequest request)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        plugin.Configuration.PublicBaseUrl = ReverseProxyHosting.NormalizePublicBaseUrl(request.PublicBaseUrl);

        if (request.EbsBackgroundMusicSource.HasValue)
        {
            plugin.Configuration.EbsBackgroundMusicSource = request.EbsBackgroundMusicSource.Value;
        }

        if (request.EbsBackgroundMusicLibraryName is not null)
        {
            plugin.Configuration.EbsBackgroundMusicLibraryName = request.EbsBackgroundMusicLibraryName.Trim();
        }

        plugin.Configuration.EbsBackgroundMusicLibraryId = string.IsNullOrWhiteSpace(request.EbsBackgroundMusicLibraryId)
            ? null
            : request.EbsBackgroundMusicLibraryId.Trim();

        if (request.WeatherStarBaseUrl is not null)
        {
            plugin.Configuration.WeatherStarBaseUrl = WeatherStarChannelService.NormalizeWeatherStarBaseUrl(request.WeatherStarBaseUrl);
        }

        if (request.WeatherStarPermalinkQuery is not null)
        {
            plugin.Configuration.WeatherStarPermalinkQuery =
                WeatherStarChannelService.NormalizePermalinkQuery(request.WeatherStarPermalinkQuery);
        }

        if (request.WeatherStarFullPermalink is not null
            && !string.IsNullOrWhiteSpace(request.WeatherStarFullPermalink))
        {
            var split = WeatherStarChannelService.SplitPermalink(request.WeatherStarFullPermalink);
            plugin.Configuration.WeatherStarBaseUrl = split.BaseUrl;
            plugin.Configuration.WeatherStarPermalinkQuery = split.Query;
        }

        if (request.AutoStartPlaywrightDockerSidecar.HasValue)
        {
            plugin.Configuration.AutoStartPlaywrightDockerSidecar = request.AutoStartPlaywrightDockerSidecar.Value;
        }

        if (request.AutoStartWeatherStarDocker.HasValue)
        {
            plugin.Configuration.AutoStartWeatherStarDocker = request.AutoStartWeatherStarDocker.Value;
        }

        if (request.WeatherStarAutoWideForSixteenNine.HasValue)
        {
            plugin.Configuration.WeatherStarAutoWideForSixteenNine = request.WeatherStarAutoWideForSixteenNine.Value;
        }

        plugin.SaveConfiguration();

        return Ok(BuildUrlResponse());
    }

    private object BuildUrlResponse()
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost);
        var localBaseUrl = ReverseProxyHosting.LocalBaseUrl(Request);
        var m3u = $"{baseUrl}/iptv/channels.m3u";
        var epg = $"{baseUrl}/iptv/epg.xml";
        var m3uLocal = $"{localBaseUrl}/iptv/channels.m3u";
        var epgLocal = $"{localBaseUrl}/iptv/epg.xml";
        if (User.Identity?.IsAuthenticated == true)
        {
            (m3u, epg) = PluginApiKey.BuildLiveTvUrls(baseUrl);
            (m3uLocal, epgLocal) = PluginApiKey.BuildLiveTvUrls(localBaseUrl);
        }

        return new
        {
            baseUrl,
            localBaseUrl,
            m3u,
            epg,
            m3uLocal,
            epgLocal,
            instructions = new[]
            {
                "Dashboard → Live TV → Add Tuner → M3U Tuner",
                "Paste the M3U Tuner URL above (must be reachable by the Jellyfin server)",
                "Dashboard → Live TV → Add Guide Provider → XMLTV",
                "Paste the XMLTV Guide URL above, then Refresh Channels and Refresh Guide"
            }
        };
    }
}

/// <summary>
/// Setup settings payload.
/// </summary>
public class SetupSettingsRequest
{
    /// <summary>
    /// Gets or sets the public base URL used in generated M3U/XMLTV links.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets where EBS off-air background music is selected from.
    /// </summary>
    public EbsBackgroundMusicSource? EbsBackgroundMusicSource { get; set; }

    /// <summary>
    /// Gets or sets the selected music library name for EBS background music.
    /// </summary>
    public string? EbsBackgroundMusicLibraryName { get; set; }

    /// <summary>
    /// Gets or sets the selected music library identifier for EBS background music.
    /// </summary>
    public string? EbsBackgroundMusicLibraryId { get; set; }

    /// <summary>
    /// Gets or sets the WeatherStar 4000 base URL used by weather channels.
    /// </summary>
    public string? WeatherStarBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the ws4kp permalink query string (display settings without location).
    /// </summary>
    public string? WeatherStarPermalinkQuery { get; set; }

    /// <summary>
    /// Gets or sets a full WeatherStar permalink; ChannelFlow splits it into base URL and display settings.
    /// </summary>
    public string? WeatherStarFullPermalink { get; set; }

    /// <summary>
    /// Gets or sets whether the Playwright Docker CDP sidecar starts during Jellyfin startup.
    /// </summary>
    public bool? AutoStartPlaywrightDockerSidecar { get; set; }

    /// <summary>
    /// Gets or sets whether the self-hosted WeatherStar Docker container starts during Jellyfin startup.
    /// </summary>
    public bool? AutoStartWeatherStarDocker { get; set; }

    /// <summary>
    /// Gets or sets whether weather capture auto-sets wide=true for 16:9 channels (and wide=false for 4:3).
    /// </summary>
    public bool? WeatherStarAutoWideForSixteenNine { get; set; }
}

/// <summary>
/// Background task endpoints for ChannelFlow maintenance.
/// </summary>
[ApiController]
[Route("api/tasks")]
[Authorize(Policy = "admin")]
public class TasksController : ControllerBase
{
    private readonly PlayoutBuilderService _playoutBuilder;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CatalogCleanupService _catalogCleanup;
    private readonly NewsBulletinService _newsBulletins;
    private readonly StreamService _streams;
    private readonly CommercialService _commercials;
    private readonly FinTvDbContext _db;
    private readonly CatalogLibraryScanService _libraryScan;
    private readonly CatalogFfprobeScanService _ffprobeScan;
    private readonly CatalogTrueAspectScanService _trueAspectScan;
    private readonly CatalogCommercialBreakScanService _commercialBreakScan;

    /// <summary>
    /// Initializes a new instance of the <see cref="TasksController"/> class.
    /// </summary>
    public TasksController(
        PlayoutBuilderService playoutBuilder,
        IServiceScopeFactory scopeFactory,
        CatalogCleanupService catalogCleanup,
        CatalogLibraryScanService libraryScan,
        CatalogFfprobeScanService ffprobeScan,
        CatalogTrueAspectScanService trueAspectScan,
        CatalogCommercialBreakScanService commercialBreakScan,
        NewsBulletinService newsBulletins,
        StreamService streams,
        CommercialService commercials,
        FinTvDbContext db)
    {
        _playoutBuilder = playoutBuilder;
        _scopeFactory = scopeFactory;
        _catalogCleanup = catalogCleanup;
        _libraryScan = libraryScan;
        _ffprobeScan = ffprobeScan;
        _trueAspectScan = trueAspectScan;
        _commercialBreakScan = commercialBreakScan;
        _newsBulletins = newsBulletins;
        _streams = streams;
        _commercials = commercials;
        _db = db;
    }

    /// <summary>
    /// Queues a background rebuild of playout timelines for all enabled channels.
    /// </summary>
    /// <returns>Accepted when the rebuild is queued.</returns>
    [HttpPost("rebuild-all")]
    public IActionResult RebuildAll()
    {
        _playoutBuilder.QueueForceRebuildAllChannels();
        return Accepted(new { queued = true });
    }

    /// <summary>
    /// Deletes every playout item and episode cursor so the Live TV guide can be rebuilt from scratch.
    /// Lineups are kept. Live encodes are cut so channels go Off Air until playout is rebuilt.
    /// </summary>
    [HttpPost("clear-guide")]
    public async Task<ActionResult<object>> ClearGuide(CancellationToken cancellationToken)
    {
        var cleared = await _playoutBuilder.ClearAllGuideDataAsync(cancellationToken);
        return Ok(new
        {
            cleared,
            message = "Guide playout cleared. Rebuild All Playouts to fill the schedule again."
        });
    }

    /// <summary>
    /// Cuts every currently watched channel to a commercial break in 15 seconds.
    /// </summary>
    [HttpPost("force-commercial")]
    public async Task<ActionResult> ForceCommercial(CancellationToken cancellationToken)
    {
        const int delaySeconds = 15;
        var watched = _streams.GetActiveStreams();
        if (watched.Count == 0)
        {
            return Ok(new
            {
                delaySeconds,
                forcedCount = 0,
                skippedCount = 0,
                forced = Array.Empty<object>(),
                skipped = Array.Empty<object>(),
                message = "No channels are currently being watched."
            });
        }

        var ids = watched.Select(stream => stream.ChannelId).ToList();
        var channels = await _db.Channels
            .Where(channel => ids.Contains(channel.Id))
            .OrderBy(channel => channel.Number)
            .ToListAsync(cancellationToken);

        var forced = new List<object>();
        var skipped = new List<object>();
        foreach (var channel in channels)
        {
            var result = await _commercials.ForceCommercialBreakAsync(
                channel,
                TimeSpan.FromSeconds(delaySeconds),
                cancellationToken);
            if (result.Forced)
            {
                _streams.InterruptCurrentItem(channel.Id);
                forced.Add(new
                {
                    channelId = channel.Id,
                    channelName = result.ChannelName,
                    delaySeconds = result.DelaySeconds,
                    message = result.Message
                });
            }
            else
            {
                skipped.Add(new
                {
                    channelId = channel.Id,
                    channelName = result.ChannelName,
                    message = result.Message
                });
            }
        }

        var message = forced.Count == 0
            ? "Could not force a commercial on any watched channel."
            : $"Forced {forced.Count} channel{(forced.Count == 1 ? "" : "s")} to commercial in {delaySeconds} seconds.";
        return Ok(new
        {
            delaySeconds,
            forcedCount = forced.Count,
            skippedCount = skipped.Count,
            forced,
            skipped,
            message
        });
    }

    [HttpPost("news-bulletin")]
    public ActionResult<object> RunNewsBulletin()
    {
        var started = _newsBulletins.TryQueue();
        return Accepted(new
        {
            started,
            alreadyRunning = !started,
            bulletin = _newsBulletins.DescribeStatus()
        });
    }

    [HttpGet("catalog-cleanup")]
    public async Task<ActionResult<object>> GetCatalogCleanup(CancellationToken cancellationToken)
        => Ok(await BuildCatalogCleanupStatusAsync(cancellationToken));

    [HttpPut("catalog-cleanup")]
    public async Task<ActionResult<object>> UpdateCatalogCleanup(
        [FromBody] CatalogCleanupSettingsRequest? request,
        CancellationToken cancellationToken)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        if (request?.GracePeriodDays is int days)
        {
            plugin.Configuration.CatalogCleanup.GracePeriodDays = CatalogCleanupService.ClampGracePeriodDays(days);
            plugin.SaveConfiguration();
        }

        return Ok(await BuildCatalogCleanupStatusAsync(cancellationToken));
    }

    [HttpPost("catalog-cleanup/run")]
    public async Task<IActionResult> RunCatalogCleanup(CancellationToken cancellationToken)
    {
        var status = await _catalogCleanup.GetStatusAsync(cancellationToken);
        if (status.IsRunning)
        {
            return Ok(new { queued = false, alreadyRunning = true, status = await BuildCatalogCleanupStatusAsync(cancellationToken) });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var cleanup = scope.ServiceProvider.GetRequiredService<CatalogCleanupService>();
                await cleanup.RunAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Admin polls status for errors.
            }
        });

        return Accepted(new { queued = true, status = await BuildCatalogCleanupStatusAsync(cancellationToken, forceCleanupRunning: true) });
    }

    private async Task<object> BuildCatalogCleanupStatusAsync(
        CancellationToken cancellationToken,
        bool forceCleanupRunning = false,
        bool forceScanRunning = false)
    {
        var status = await _catalogCleanup.GetStatusAsync(cancellationToken);
        return new
        {
            gracePeriodDays = status.GracePeriodDays,
            isRunning = status.IsRunning || forceCleanupRunning,
            markedMissing = status.MarkedMissing,
            removed = status.Removed,
            currentlyMissing = status.CurrentlyMissing,
            lastError = status.LastError,
            lastStartedAt = status.LastStartedAt,
            lastCompletedAt = status.LastCompletedAt,
            lastCatalogSyncStartedAt = status.LastCatalogSyncStartedAt,
            lastCatalogSyncCompletedAt = status.LastCatalogSyncCompletedAt,
            localScan = new
            {
                isRunning = status.LocalScanIsRunning || forceScanRunning,
                totalItems = status.LocalScanTotalItems,
                processedItems = status.LocalScanProcessedItems,
                found = status.LocalScanFound,
                markedMissing = status.LocalScanMarkedMissing,
                restored = status.LocalScanRestored,
                skipped = status.LocalScanSkipped,
                lastError = status.LocalScanLastError,
                lastStartedAt = status.LocalScanLastStartedAt,
                lastCompletedAt = status.LocalScanLastCompletedAt
            }
        };
    }

    [HttpPost("catalog-cleanup/scan-local")]
    public async Task<IActionResult> ScanLocalCatalogFiles(CancellationToken cancellationToken)
    {
        var status = await _catalogCleanup.GetStatusAsync(cancellationToken);
        if (status.IsRunning || status.LocalScanIsRunning)
        {
            return Ok(new { queued = false, alreadyRunning = true, status = await BuildCatalogCleanupStatusAsync(cancellationToken) });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var cleanup = scope.ServiceProvider.GetRequiredService<CatalogCleanupService>();
                await cleanup.ScanLocalFilesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Admin polls status for errors.
            }
        });

        return Accepted(new { queued = true, status = await BuildCatalogCleanupStatusAsync(cancellationToken, forceScanRunning: true) });
    }

    /// <summary>
    /// Gets the six-hour library connection scan status.
    /// </summary>
    [HttpGet("library-scan")]
    public ActionResult<object> GetLibraryScan()
        => Ok(_libraryScan.Describe());

    /// <summary>
    /// Queues a catalog sync of every enabled library connection.
    /// </summary>
    [HttpPost("library-scan")]
    public ActionResult<object> RunLibraryScan()
    {
        if (_libraryScan.IsRunning)
        {
            return Ok(new { queued = false, alreadyRunning = true, status = _libraryScan.Describe() });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _libraryScan.RunAllAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Admin polls status for errors.
            }
        });

        return Accepted(new { queued = true, status = _libraryScan.Describe() });
    }

    /// <summary>
    /// Gets the midnight ffprobe chapter scan status.
    /// </summary>
    [HttpGet("ffprobe")]
    public ActionResult<object> GetFfprobeScan()
        => Ok(_ffprobeScan.Describe());

    /// <summary>
    /// Queues an ffprobe pass over video files that still have no chapter data.
    /// </summary>
    [HttpPost("ffprobe")]
    public ActionResult<object> RunFfprobeScan()
    {
        if (!_ffprobeScan.TryBegin())
        {
            return Ok(new { queued = false, alreadyRunning = true, status = _ffprobeScan.Describe() });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _ffprobeScan.RunMissingAsync(begin: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Admin polls status for errors.
            }
        });

        return Accepted(new { queued = true, status = _ffprobeScan.Describe() });
    }

    /// <summary>
    /// Gets the midnight true-aspect cropdetect scan status.
    /// </summary>
    [HttpGet("true-aspect")]
    public ActionResult<object> GetTrueAspectScan()
        => Ok(_trueAspectScan.Describe());

    /// <summary>
    /// Queues a cropdetect pass over videos that still have no TrueAspectRatio.
    /// </summary>
    [HttpPost("true-aspect")]
    public ActionResult<object> RunTrueAspectScan()
    {
        if (!_trueAspectScan.TryBegin())
        {
            return Ok(new { queued = false, alreadyRunning = true, status = _trueAspectScan.Describe() });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _trueAspectScan.RunMissingAsync(begin: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Admin polls status for errors.
            }
        });

        return Accepted(new { queued = true, status = _trueAspectScan.Describe() });
    }

    /// <summary>
    /// Gets commercial-break scan status and detector settings.
    /// </summary>
    [HttpGet("commercial-breaks")]
    public ActionResult<object> GetCommercialBreakScan()
        => Ok(_commercialBreakScan.Describe());

    /// <summary>
    /// Queues a blackdetect+silencedetect pass over videos that still have no commercial-break scan.
    /// </summary>
    [HttpPost("commercial-breaks")]
    public ActionResult<object> RunCommercialBreakScan()
    {
        var settings = FinTvRuntime.Current?.Configuration.CommercialBreakScan;
        if (settings is null || !settings.ScanEnabled)
        {
            return Ok(new
            {
                queued = false,
                disabled = true,
                status = _commercialBreakScan.Describe()
            });
        }

        if (!_commercialBreakScan.TryBegin())
        {
            return Ok(new { queued = false, alreadyRunning = true, status = _commercialBreakScan.Describe() });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _commercialBreakScan.RunMissingAsync(begin: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Admin polls status for errors.
            }
        });

        return Accepted(new { queued = true, status = _commercialBreakScan.Describe() });
    }

    [HttpGet("commercial-breaks/settings")]
    public ActionResult<CommercialBreakScanSettings> GetCommercialBreakSettings()
        => Ok(CloneSettings(FinTvRuntime.Current?.Configuration.CommercialBreakScan));

    [HttpPut("commercial-breaks/settings")]
    public ActionResult<CommercialBreakScanSettings> UpdateCommercialBreakSettings(
        [FromBody] CommercialBreakScanSettings? request)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        var next = request ?? new CommercialBreakScanSettings();
        next.Clamp();
        plugin.Configuration.CommercialBreakScan = next;
        plugin.SaveConfiguration();
        return Ok(CloneSettings(next));
    }

    private static CommercialBreakScanSettings CloneSettings(CommercialBreakScanSettings? source)
    {
        var settings = source ?? new CommercialBreakScanSettings();
        settings.Clamp();
        return new CommercialBreakScanSettings
        {
            ScanEnabled = settings.ScanEnabled,
            WriteChaptersToFiles = settings.WriteChaptersToFiles,
            SilenceDb = settings.SilenceDb,
            SilenceMinSeconds = settings.SilenceMinSeconds,
            BlackPixThreshold = settings.BlackPixThreshold,
            BlackPictureRatio = settings.BlackPictureRatio,
            BlackMinFrames = settings.BlackMinFrames,
            ConfidencePercent = settings.ConfidencePercent,
            MinGapSeconds = settings.MinGapSeconds
        };
    }
}

public class CatalogCleanupSettingsRequest
{
    public int? GracePeriodDays { get; set; }
}
