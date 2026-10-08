using System.Collections.Concurrent;
using System.Text;
using CliWrap;
using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using FinTv.News;
using FinTv.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public class StreamService : IDisposable
{
    private readonly ConcurrentDictionary<Guid, int> _activeStreams = new();
    private readonly ConcurrentDictionary<Guid, ChannelLiveSession> _liveSessions = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _itemCuts = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FfmpegCommandBuilder _ffmpeg;
    private readonly WeatherAlertOverlayService _weatherAlerts;
    private readonly ILogger<StreamService> _logger;
    private readonly IFfmpegLocator _mediaEncoder;
    private readonly object _punchGate = new();
    private DateTime _lastPunchInUtc = DateTime.MinValue;
    private int _disposed;

    public StreamService(
        IServiceScopeFactory scopeFactory,
        FfmpegCommandBuilder ffmpeg,
        WeatherAlertOverlayService weatherAlerts,
        ILogger<StreamService> logger,
        IFfmpegLocator mediaEncoder)
    {
        _scopeFactory = scopeFactory;
        _ffmpeg = ffmpeg;
        _weatherAlerts = weatherAlerts;
        _logger = logger;
        _mediaEncoder = mediaEncoder;
    }

    internal static int GetIdleTimeoutSeconds()
    {
        var configured = FinTvRuntime.Current?.Configuration.StreamIdleTimeoutSeconds ?? 30;
        return PluginConfiguration.ClampStreamIdleTimeoutSeconds(configured);
    }

    internal static int GetRunAheadSeconds()
    {
        var configured = FinTvRuntime.Current?.Configuration.Transcode?.RunAheadSeconds
            ?? TranscodeSettings.DefaultRunAheadSeconds;
        if (configured > 0 && configured < TranscodeSettings.DefaultRunAheadSeconds)
        {
            configured = TranscodeSettings.DefaultRunAheadSeconds;
        }

        return TranscodeSettings.ClampRunAheadSeconds(configured);
    }

    internal const int WeatherAlertRunAheadSeconds = 60;

    /// <summary>
    /// MPEG-TS bytes per second used to size the run-ahead ring and pace ffmpeg.
    /// Sized above the 5 Mbps video maxrate plus audio and TS overhead so CRF spikes still buffer.
    /// </summary>
    internal const int RunAheadBytesPerSecond = 1_250_000;

    internal static int GetRunAheadRingBytes()
    {
        var seconds = Math.Max(1, GetRunAheadSeconds());
        return seconds * RunAheadBytesPerSecond;
    }

    public async Task StreamChannelAsync(Guid channelId, Stream output, CancellationToken cancellationToken)
    {
        using var streamLease = TrackStream(channelId);
        while (!cancellationToken.IsCancellationRequested)
        {
            var session = _liveSessions.GetOrAdd(channelId, CreateLiveSession);
            if (await session.AttachViewerAsync(output, cancellationToken))
            {
                return;
            }

            _liveSessions.TryRemove(new KeyValuePair<Guid, ChannelLiveSession>(channelId, session));
        }
    }

    public async Task<bool> ChannelExistsAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        return await db.Channels.AsNoTracking().AnyAsync(c => c.Id == channelId, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var session in _liveSessions.Values.ToArray())
        {
            session.ForceStop();
        }
    }

    private ChannelLiveSession CreateLiveSession(Guid channelId)
    {
        return new ChannelLiveSession(
            channelId,
            (output, token) => EncodeChannelAsync(channelId, output, token),
            session => _liveSessions.TryRemove(new KeyValuePair<Guid, ChannelLiveSession>(channelId, session)),
            _logger);
    }

    private async Task EncodeChannelAsync(Guid channelId, Stream output, CancellationToken cancellationToken)
    {
        Channel channel;
        using (var lookup = _scopeFactory.CreateScope())
        {
            var db = lookup.ServiceProvider.GetRequiredService<FinTvDbContext>();
            var found = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
            if (found is null)
            {
                _logger.LogWarning("IPTV stream requested for missing channel {ChannelId}", channelId);
                throw new InvalidOperationException("Channel not found.");
            }

            channel = found;
        }

        if (channel.IsContinuousLive && !string.IsNullOrWhiteSpace(channel.RtspUrl))
        {
            var rtspUrl = channel.RtspUrl;
            await StreamUntilCanceledAsync(
                "RTSP",
                channel.Name,
                async () =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var rtsp = scope.ServiceProvider.GetRequiredService<RtspChannelService>();
                    await rtsp.StreamAsync(channel, rtspUrl, output, cancellationToken);
                },
                cancellationToken);
            return;
        }

        if (channel.ContentType == ChannelContentType.Weather)
        {
            await StreamUntilCanceledAsync(
                "Weather",
                channel.Name,
                async () =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var weather = scope.ServiceProvider.GetRequiredService<WeatherStarChannelService>();
                    await weather.StreamAsync(channel, output, cancellationToken);
                },
                cancellationToken);
            return;
        }

        if (channel.ContentType == ChannelContentType.News)
        {
            await StreamUntilCanceledAsync(
                "News",
                channel.Name,
                async () =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var news = scope.ServiceProvider.GetRequiredService<NewsChannelService>();
                    await news.StreamAsync(channel, output, cancellationToken);
                },
                cancellationToken);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<JellyfinCatalogService>();
        var weather = scope.ServiceProvider.GetRequiredService<WeatherStarChannelService>();
        var ebs = scope.ServiceProvider.GetRequiredService<EbsService>();
        var youtubeCommercials = scope.ServiceProvider.GetRequiredService<YouTubeCommercialStreamService>();
        var holidays = scope.ServiceProvider.GetRequiredService<HolidayChannelService>();

        var ffmpegPath = _mediaEncoder.EncoderPath;
        var alertSession = new WeatherAlertCutInSession();

        while (!cancellationToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            if (await _weatherAlerts.ShouldCutInNowAsync(channel, alertSession, cancellationToken))
            {
                PunchInWeatherAlert();
                using var itemCts = CreateItemCutCts(channelId);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, itemCts.Token);
                try
                {
                    if (_weatherAlerts.EffectiveMode == WeatherAlertOverlayMode.Ticker)
                    {
                        var tickerItem = await GetCurrentItemAsync(channelId, cancellationToken);
                        if (tickerItem is not null)
                        {
                            var tones = await weather.CreateToneSandwichAsync(
                                _weatherAlerts.CutInDurationForStream.TotalSeconds,
                                linked.Token);
                            await StreamTickerAlertWindowAsync(
                                channel,
                                tickerItem,
                                catalog,
                                youtubeCommercials,
                                holidays,
                                ebs,
                                ffmpegPath,
                                output,
                                tones,
                                linked.Token);
                        }
                    }
                    else
                    {
                        var screenItem = await GetCurrentItemAsync(channelId, cancellationToken);
                        if (screenItem is not null)
                        {
                            var tones = await weather.CreateToneSandwichAsync(
                                _weatherAlerts.CutInDurationForStream.TotalSeconds,
                                linked.Token);
                            await StreamFullscreenAlertWindowAsync(
                                channel,
                                screenItem,
                                catalog,
                                ffmpegPath,
                                output,
                                tones,
                                linked.Token);
                        }
                    }

                    if (!linked.IsCancellationRequested)
                    {
                        _weatherAlerts.MarkCutInComplete(alertSession);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Test stop or item cut — keep the shared encoder running.
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Weather alert overlay failed for {Channel}", channel.Name);
                    _weatherAlerts.MarkCutInComplete(alertSession);
                }
                finally
                {
                    RestoreRunAheadBuffer(channelId);
                }

                continue;
            }

            var current = await GetCurrentItemAsync(channelId, cancellationToken);
            var skipDelay = false;
            if (current is not null)
            {
                await PrefetchUpcomingYouTubeCommercialsAsync(
                    channel,
                    current,
                    youtubeCommercials,
                    cancellationToken);
                using var itemCts = CreateItemCutCts(channelId);
                var remaining = current.Finish.Kind == DateTimeKind.Utc
                    ? current.Finish - DateTime.UtcNow
                    : DateTime.SpecifyKind(current.Finish, DateTimeKind.Utc) - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero && remaining < TimeSpan.FromDays(2))
                {
                    itemCts.CancelAfter(remaining);
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, itemCts.Token);
                try
                {
                    if (current.IsVirtual && current.VirtualSource == VirtualContentSource.MusicArtSlide)
                    {
                        await StreamMusicItemAsync(channel, current, catalog, ffmpegPath, output, linked.Token);
                    }
                    else if (current.IsVirtual && current.VirtualSource == VirtualContentSource.LogoBumper)
                    {
                        await StreamLogoBumperAsync(channel, current, ffmpegPath, output, linked.Token);
                    }
                    else if (current.IsVirtual && current.VirtualSource == VirtualContentSource.BundledVideo)
                    {
                        await StreamBundledVideoAsync(channel, current, ffmpegPath, output, linked.Token);
                    }
                    else if (current.IsVirtual && current.VirtualSource == VirtualContentSource.YouTubeMusicVideo)
                    {
                        await StreamYouTubeMusicVideoAsync(channel, current, youtubeCommercials, ffmpegPath, output, linked.Token);
                    }
                    else if (current.CommercialId.HasValue)
                    {
                        await StreamCommercialItemAsync(channel, current, catalog, youtubeCommercials, ffmpegPath, output, linked.Token);
                    }
                    else if (current.JellyfinItemId.HasValue)
                    {
                        await StreamMediaItemAsync(channel, current, catalog, holidays, ffmpegPath, output, alertSession, linked.Token);
                    }
                    else
                    {
                        await WriteEbsAsync(channel, ebs, ffmpegPath, output, 180, linked.Token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    skipDelay = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed streaming item {Title}", current.Title);
                    await WriteEbsAsync(channel, ebs, ffmpegPath, output, 120, cancellationToken);
                }
            }
            else
            {
                var ebsDuration = await GetEbsDurationSecondsAsync(channelId, cancellationToken);
                await WriteEbsAsync(channel, ebs, ffmpegPath, output, ebsDuration, cancellationToken);
            }

            if (!skipDelay)
            {
                await DelayIfStreamEndedImmediatelyAsync(channel.Name, current, started, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Streams a single scheduled playout item to the output until its window ends or the
    /// caller disconnects. Used by the ErsatzTV next live source (itemId mode) so a
    /// commercial/art-slide/bumper resolved ahead of its wall-clock slot starts from its own
    /// beginning instead of leaking the previous item's tail.
    /// </summary>
    public async Task StreamNextItemAsync(
        Guid channelId,
        Guid itemId,
        Stream output,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        var item = await db.PlayoutItems.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == itemId && p.ChannelId == channelId, cancellationToken);
        if (channel is null || item is null)
        {
            _logger.LogWarning("Next source requested missing item {ItemId} on channel {ChannelId}", itemId, channelId);
            return;
        }

        var remaining = item.Finish.Kind == DateTimeKind.Utc
            ? item.Finish - DateTime.UtcNow
            : DateTime.SpecifyKind(item.Finish, DateTimeKind.Utc) - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            remaining = TimeSpan.FromSeconds(5);
        }

        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (remaining < TimeSpan.FromDays(2))
        {
            itemCts.CancelAfter(remaining);
        }

        var catalog = scope.ServiceProvider.GetRequiredService<JellyfinCatalogService>();
        var ebs = scope.ServiceProvider.GetRequiredService<EbsService>();
        var youtubeCommercials = scope.ServiceProvider.GetRequiredService<YouTubeCommercialStreamService>();
        var holidays = scope.ServiceProvider.GetRequiredService<HolidayChannelService>();
        var ffmpegPath = _mediaEncoder.EncoderPath;

        try
        {
            if (item.IsVirtual && item.VirtualSource == VirtualContentSource.MusicArtSlide)
            {
                await StreamMusicItemAsync(channel, item, catalog, ffmpegPath, output, itemCts.Token);
            }
            else if (item.IsVirtual && item.VirtualSource == VirtualContentSource.LogoBumper)
            {
                await StreamLogoBumperAsync(channel, item, ffmpegPath, output, itemCts.Token);
            }
            else if (item.IsVirtual && item.VirtualSource == VirtualContentSource.BundledVideo)
            {
                await StreamBundledVideoAsync(channel, item, ffmpegPath, output, itemCts.Token);
            }
            else if (item.IsVirtual && item.VirtualSource == VirtualContentSource.YouTubeMusicVideo)
            {
                await StreamYouTubeMusicVideoAsync(channel, item, youtubeCommercials, ffmpegPath, output, itemCts.Token);
            }
            else if (item.CommercialId.HasValue)
            {
                await StreamCommercialItemAsync(channel, item, catalog, youtubeCommercials, ffmpegPath, output, itemCts.Token);
            }
            else if (item.JellyfinItemId.HasValue)
            {
                await StreamMediaItemAsync(channel, item, catalog, holidays, ffmpegPath, output, new WeatherAlertCutInSession(), itemCts.Token);
            }
            else
            {
                await WriteEbsAsync(channel, ebs, ffmpegPath, output, 180, itemCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Item window expired (normal) or the caller disconnected; end the source.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Next source failed streaming item {Title}", item.Title);
            try
            {
                await WriteEbsAsync(channel, ebs, ffmpegPath, output, 120, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task StreamUntilCanceledAsync(
        string kind,
        string channelName,
        Func<Task> stream,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                await stream();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Kind} stream failed for {Channel}; retrying in 5 seconds", kind, channelName);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }

            await DelayIfStreamEndedImmediatelyAsync(channelName, item: null, started, cancellationToken);
        }
    }

    private async Task DelayIfStreamEndedImmediatelyAsync(
        string channelName,
        PlayoutItem? item,
        DateTime startedUtc,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - startedUtc;
        if (elapsed >= TimeSpan.FromSeconds(3))
        {
            return;
        }

        if (item?.CommercialId is not null
            || (item?.IsVirtual == true
                && (item.VirtualSource == VirtualContentSource.LogoBumper
                    || item.VirtualSource == VirtualContentSource.BundledVideo
                    || item.VirtualSource == VirtualContentSource.YouTubeMusicVideo)))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            return;
        }

        _logger.LogWarning(
            "Stream ended after {ElapsedMs:0}ms for {Channel}; retrying in 5 seconds",
            elapsed.TotalMilliseconds,
            channelName);
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }

    private async Task PrefetchUpcomingYouTubeCommercialsAsync(
        Channel channel,
        PlayoutItem current,
        YouTubeCommercialStreamService youtubeCommercials,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var horizon = DateTime.UtcNow.AddSeconds(90);
        var upcoming = await db.PlayoutItems.AsNoTracking()
            .Where(p => p.ChannelId == channel.Id
                && p.CommercialId != null
                && p.Id != current.Id
                && p.Start <= horizon
                && p.Finish > DateTime.UtcNow.AddSeconds(-1))
            .OrderBy(p => p.Start)
            .Take(4)
            .ToListAsync(cancellationToken);

        if (upcoming.Count == 0)
        {
            return;
        }

        var ids = upcoming.Select(p => p.CommercialId!.Value).Distinct().ToList();
        var commercials = await db.Commercials.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(cancellationToken);
        var byId = commercials.ToDictionary(c => c.Id);
        foreach (var item in upcoming)
        {
            if (!byId.TryGetValue(item.CommercialId!.Value, out var commercial))
            {
                continue;
            }

            var duration = Math.Max(1, (item.Finish - DateTime.UtcNow).TotalSeconds);
            youtubeCommercials.BeginPrefetch(commercial, duration, cancellationToken);
        }
    }

    public async Task<PlayoutItem?> GetCurrentItemAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var now = DateTime.UtcNow;
        return await db.PlayoutItems
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId && p.Start <= now && p.Finish > now)
            .OrderByDescending(p => p.Start)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Finds the playout item airing on a channel at an arbitrary UTC instant. Used by the
    /// ErsatzTV next resolver, which asks "what should play at time X" for its transcode focus.
    /// </summary>
    public async Task<PlayoutItem?> GetItemAtAsync(
        Guid channelId,
        DateTime atUtc,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        return await db.PlayoutItems
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId && p.Start <= atUtc && p.Finish > atUtc)
            .OrderByDescending(p => p.Start)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Finds when the next scheduled item after a UTC instant starts (used to bound an
    /// off-air/EBS live source so the resolver is re-hit at the next boundary).
    /// </summary>
    public async Task<DateTime?> GetNextItemStartAsync(
        Guid channelId,
        DateTime atUtc,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        return await db.PlayoutItems
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId && p.Start > atUtc)
            .OrderBy(p => p.Start)
            .Select(p => (DateTime?)p.Start)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public IReadOnlyList<ChannelStreamStatus> GetActiveStreams()
    {
        return _activeStreams
            .Where(kvp => kvp.Value > 0)
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new ChannelStreamStatus
            {
                ChannelId = kvp.Key,
                ViewerCount = kvp.Value
            })
            .ToList();
    }

    public int GetActiveStreamCount(Guid channelId)
    {
        return _activeStreams.TryGetValue(channelId, out var count) ? count : 0;
    }

    /// <summary>
    /// Stops the current ffmpeg item so playout is re-read. Viewers stay on the shared session.
    /// </summary>
    public void InterruptCurrentItem(Guid channelId)
    {
        if (_itemCuts.TryGetValue(channelId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (_liveSessions.TryGetValue(channelId, out var session))
        {
            session.DropReplayAndResetPace();
        }

        _logger.LogInformation("Cutting the current encode on {ChannelId} so it matches the new playout", channelId);
    }

    /// <summary>
    /// Drops every live run-ahead ring to 60 seconds and cuts current encodes so the
    /// EBS graphic can splice onto the same program without tearing down the session.
    /// </summary>
    public void PunchInWeatherAlert()
    {
        _weatherAlerts.BeginBackgroundPrerender();
        var punch = false;
        lock (_punchGate)
        {
            if (DateTime.UtcNow - _lastPunchInUtc > TimeSpan.FromSeconds(2))
            {
                _lastPunchInUtc = DateTime.UtcNow;
                punch = true;
            }
        }

        if (!punch)
        {
            return;
        }

        foreach (var id in _liveSessions.Keys.ToArray())
        {
            FlushBufferForWeatherAlert(id);
        }

        InterruptAllCurrentItems();
    }

    /// <summary>
    /// Drops the run-ahead ring to 60 seconds so an EBS alert is not stuck behind the full tuner buffer.
    /// Another alert while the buffer is refilling drops it again.
    /// </summary>
    private void FlushBufferForWeatherAlert(Guid channelId)
    {
        if (!_liveSessions.TryGetValue(channelId, out var session))
        {
            return;
        }

        session.FlushForWeatherAlert(WeatherAlertRunAheadSeconds);
    }

    private void RestoreRunAheadBuffer(Guid channelId)
    {
        if (_liveSessions.TryGetValue(channelId, out var session))
        {
            session.RestoreRunAhead();
        }
    }

    /// <summary>
    /// Cuts every live encode so each channel re-reads playout (or goes Off Air if none remains).
    /// </summary>
    public void InterruptAllCurrentItems()
    {
        var ids = _itemCuts.Keys.Concat(_liveSessions.Keys).Distinct().ToArray();
        foreach (var id in ids)
        {
            InterruptCurrentItem(id);
        }
    }

    private CancellationTokenSource CreateItemCutCts(Guid channelId)
    {
        var cts = new CancellationTokenSource();
        _itemCuts[channelId] = cts;
        return cts;
    }

    private StreamLease TrackStream(Guid channelId)
    {
        _activeStreams.AddOrUpdate(channelId, 1, (_, count) => count + 1);
        return new StreamLease(this, channelId);
    }

    private void ReleaseStream(Guid channelId)
    {
        _activeStreams.AddOrUpdate(channelId, 0, (_, count) => Math.Max(0, count - 1));
        if (_activeStreams.TryGetValue(channelId, out var remaining) && remaining == 0)
        {
            _activeStreams.TryRemove(channelId, out _);
        }
    }

    private sealed class StreamLease : IDisposable
    {
        private readonly StreamService _service;
        private readonly Guid _channelId;
        private int _disposed;

        public StreamLease(StreamService service, Guid channelId)
        {
            _service = service;
            _channelId = channelId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _service.ReleaseStream(_channelId);
        }
    }

    private async Task<double> GetEbsDurationSecondsAsync(Guid channelId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var now = DateTime.UtcNow;
        var nextStart = await db.PlayoutItems
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId && p.Start > now)
            .OrderBy(p => p.Start)
            .Select(p => p.Start)
            .FirstOrDefaultAsync(cancellationToken);

        if (nextStart == default)
        {
            return 600;
        }

        return Math.Clamp((nextStart - now).TotalSeconds, 30, 600);
    }

    private async Task StreamTickerAlertWindowAsync(
        Channel channel,
        PlayoutItem item,
        JellyfinCatalogService catalog,
        YouTubeCommercialStreamService youtubeCommercials,
        HolidayChannelService holidays,
        EbsService ebs,
        string ffmpegPath,
        Stream output,
        WeatherAlertToneSandwich? alertTones,
        CancellationToken cancellationToken)
    {
        var tickerPath = await _weatherAlerts.PrepareTickerFileAsync(channel, cancellationToken);
        var duration = _weatherAlerts.CutInDurationForStream;
        if (item.IsVirtual && item.VirtualSource == VirtualContentSource.MusicArtSlide)
        {
            await StreamMusicItemAsync(
                channel,
                item,
                catalog,
                ffmpegPath,
                output,
                cancellationToken,
                tickerPath,
                overlayChannelLogo: false,
                durationOverride: duration,
                alertTones: alertTones);
            return;
        }

        if (item.IsVirtual && item.VirtualSource == VirtualContentSource.LogoBumper)
        {
            await StreamLogoBumperAsync(channel, item, ffmpegPath, output, cancellationToken, duration);
            return;
        }

        if (item.IsVirtual && item.VirtualSource == VirtualContentSource.BundledVideo)
        {
            await StreamBundledVideoAsync(channel, item, ffmpegPath, output, cancellationToken, duration);
            return;
        }

        if (item.CommercialId.HasValue)
        {
            await StreamCommercialItemAsync(channel, item, catalog, youtubeCommercials, ffmpegPath, output, cancellationToken);
            return;
        }

        if (item.JellyfinItemId.HasValue)
        {
            await StreamMediaItemAsync(
                channel,
                item,
                catalog,
                holidays,
                ffmpegPath,
                output,
                alertSession: null,
                cancellationToken,
                durationOverride: duration,
                alertTickerPath: tickerPath,
                overlayBug: false,
                alertTones: alertTones);
            return;
        }

        await WriteEbsAsync(channel, ebs, ffmpegPath, output, duration.TotalSeconds, cancellationToken);
    }

    private async Task StreamFullscreenAlertWindowAsync(
        Channel channel,
        PlayoutItem item,
        JellyfinCatalogService catalog,
        string ffmpegPath,
        Stream output,
        WeatherAlertToneSandwich? alertTones,
        CancellationToken cancellationToken)
    {
        var graphic = await _weatherAlerts.PrepareFullscreenFileAsync(channel, cancellationToken);
        if (string.IsNullOrWhiteSpace(graphic) || !File.Exists(graphic))
        {
            _logger.LogWarning("EBS alert screen skipped for {Channel}; prerendered fullscreen PNG was not ready", channel.Name);
            return;
        }

        if (item.IsVirtual && item.VirtualSource == VirtualContentSource.LogoBumper)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
        string? inputPath = null;
        if (item.IsVirtual && item.VirtualSource == VirtualContentSource.BundledVideo)
        {
            inputPath = LogoSetService.ResolveBinarygeek119File(HolidayChannelCalendar.OffSeasonVideoRelativePath);
        }
        else if (item.JellyfinItemId.HasValue)
        {
            var mediaItem = libraryManager.GetItemById(item.JellyfinItemId.Value);
            inputPath = mediaItem is null ? null : mediaItem.Path;
        }
        else if (item.CommercialId.HasValue)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            _logger.LogWarning("EBS alert screen skipped for {Channel}; current item has no local media", channel.Name);
            return;
        }

        var offset = Math.Max(0, (DateTime.UtcNow - item.Start).TotalSeconds + item.InPoint.TotalSeconds);
        var duration = _weatherAlerts.CutInDurationForStream.TotalSeconds;
        var args = _ffmpeg.BuildFullscreenAlertCommand(
            channel,
            inputPath,
            offset,
            duration,
            graphic,
            alertTones);
        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private async Task StreamLogoBumperAsync(
        Channel channel,
        PlayoutItem item,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken,
        TimeSpan? durationOverride = null)
    {
        await StreamLocalMediaFileAsync(
            channel,
            item,
            ffmpegPath,
            output,
            cancellationToken,
            LogoBumperService.ResolveToonTakeoverPath(),
            "Slappy's Toon Takeover bumper was not found.",
            durationOverride);
    }

    private async Task StreamYouTubeMusicVideoAsync(
        Channel channel,
        PlayoutItem item,
        YouTubeCommercialStreamService youtubeCommercials,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.ExternalUrl))
        {
            throw new InvalidOperationException($"YouTube music video {item.Title} has no URL.");
        }

        YouTubeUrlHelper.TryGetVideoId(item.ExternalUrl, out var videoId);
        var duration = Math.Max(1, (item.Finish - DateTime.UtcNow).TotalSeconds);
        await youtubeCommercials.StreamUrlAsync(
            channel,
            item.ExternalUrl,
            videoId,
            _ffmpeg,
            ffmpegPath,
            duration,
            output,
            cancellationToken);
    }

    private async Task StreamBundledVideoAsync(
        Channel channel,
        PlayoutItem item,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken,
        TimeSpan? durationOverride = null)
    {
        await StreamLocalMediaFileAsync(
            channel,
            item,
            ffmpegPath,
            output,
            cancellationToken,
            LogoSetService.ResolveBinarygeek119File(HolidayChannelCalendar.OffSeasonVideoRelativePath),
            "The Holiday Channel off-season video was not found.",
            durationOverride);
    }

    private async Task StreamLocalMediaFileAsync(
        Channel channel,
        PlayoutItem item,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken,
        string? inputPath,
        string missingMessage,
        TimeSpan? durationOverride = null)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            throw new FileNotFoundException(missingMessage);
        }

        var offset = Math.Max(0, (DateTime.UtcNow - item.Start).TotalSeconds + item.InPoint.TotalSeconds);
        var duration = durationOverride?.TotalSeconds
            ?? Math.Max(1, (item.Finish - DateTime.UtcNow).TotalSeconds);
        var args = _ffmpeg.BuildMediaCommand(
            channel,
            inputPath,
            offset,
            Math.Max(1, duration),
            bugImagePath: null,
            overlayBug: false);
        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private async Task StreamMediaItemAsync(
        Channel channel,
        PlayoutItem item,
        JellyfinCatalogService catalog,
        HolidayChannelService holidays,
        string ffmpegPath,
        Stream output,
        WeatherAlertCutInSession? alertSession,
        CancellationToken cancellationToken,
        TimeSpan? durationOverride = null,
        string? alertTickerPath = null,
        bool overlayBug = true,
        WeatherAlertToneSandwich? alertTones = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
        var mediaItem = libraryManager.GetItemById(item.JellyfinItemId!.Value);
        if (mediaItem is null)
        {
            throw new InvalidOperationException($"Media item {item.JellyfinItemId} not found.");
        }

        var inputPath = catalog.GetMediaPath(mediaItem);
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Media path missing for {item.Title}.");
        }

        var offset = Math.Max(0, (DateTime.UtcNow - item.Start).TotalSeconds + item.InPoint.TotalSeconds);
        var duration = durationOverride?.TotalSeconds
            ?? Math.Max(1, (item.Finish - DateTime.UtcNow).TotalSeconds);
        if (durationOverride is null && alertSession is not null)
        {
            duration = await _weatherAlerts.CapMediaDurationAsync(channel, alertSession, duration, cancellationToken);
        }

        duration = Math.Max(1, duration);
        var bugPath = overlayBug ? ResolveBugPath(channel, item.Start, holidays) : null;
        var headline = PastTenseNewsCatalog.IsPastTenseNewsChannel(channel) ? item.Title : null;
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var (fadeBugIn, fadeBugOut) = overlayBug
            ? await GetChannelBugCommercialFadesAsync(db, item, duration, cancellationToken)
            : (false, false);
        var args = _ffmpeg.BuildMediaCommand(
            channel,
            inputPath,
            offset,
            duration,
            bugPath,
            headline,
            alertTickerPath,
            mediaItem.AspectRatio,
            mediaItem.Width,
            mediaItem.Height,
            overlayBug: overlayBug,
            fadeBugIn: fadeBugIn,
            fadeBugOut: fadeBugOut,
            alertTones: alertTones,
            sourceVideoCodec: mediaItem.VideoCodec,
            trueAspectRatio: mediaItem.TrueAspectRatio);

        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private async Task StreamCommercialItemAsync(
        Channel channel,
        PlayoutItem item,
        JellyfinCatalogService catalog,
        YouTubeCommercialStreamService youtubeCommercials,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var commercial = await db.Commercials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == item.CommercialId, cancellationToken);

        if (commercial is null)
        {
            throw new InvalidOperationException($"Commercial {item.CommercialId} not found.");
        }

        if (commercial.Source == CommercialSource.Jellyfin)
        {
            var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
            var scopedCatalog = scope.ServiceProvider.GetRequiredService<JellyfinCatalogService>();
            var mediaItem = libraryManager.GetItemById(commercial.JellyfinItemId);
            if (mediaItem is null)
            {
                throw new InvalidOperationException($"Media item {commercial.JellyfinItemId} not found.");
            }

            var inputPath = scopedCatalog.GetMediaPath(mediaItem);
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                throw new FileNotFoundException($"Media path missing for {commercial.Title}.");
            }

            var offset = Math.Max(0, (DateTime.UtcNow - item.Start).TotalSeconds + item.InPoint.TotalSeconds);
            var duration = Math.Max(1, (item.Finish - DateTime.UtcNow).TotalSeconds);
            var args = _ffmpeg.BuildMediaCommand(
                channel,
                inputPath,
                offset,
                duration,
                bugImagePath: null,
                sourceAspectRatio: mediaItem.AspectRatio,
                sourceWidth: mediaItem.Width,
                sourceHeight: mediaItem.Height,
                overlayBug: false,
                sourceVideoCodec: mediaItem.VideoCodec,
                trueAspectRatio: mediaItem.TrueAspectRatio);
            await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
            return;
        }

        await youtubeCommercials.StreamCommercialAsync(
            channel,
            commercial,
            _ffmpeg,
            ffmpegPath,
            Math.Max(1, (item.Finish - DateTime.UtcNow).TotalSeconds),
            output,
            cancellationToken);
    }

    private async Task StreamMusicItemAsync(
        Channel channel,
        PlayoutItem item,
        JellyfinCatalogService catalog,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken,
        string? alertTickerPath = null,
        bool overlayChannelLogo = true,
        TimeSpan? durationOverride = null,
        WeatherAlertToneSandwich? alertTones = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
        var scopedCatalog = scope.ServiceProvider.GetRequiredService<JellyfinCatalogService>();
        var mediaItem = libraryManager.GetItemById(item.JellyfinItemId!.Value);
        if (mediaItem is null)
        {
            throw new InvalidOperationException($"Music item {item.JellyfinItemId} not found.");
        }

        var inputPath = scopedCatalog.GetMediaPath(mediaItem);
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Music path missing for {item.Title}.");
        }

        var albumArt = scopedCatalog.GetPrimaryImagePath(mediaItem);
        var args = _ffmpeg.BuildMusicCommand(
            channel,
            inputPath,
            albumArt,
            alertTickerPath,
            overlayChannelLogo,
            durationOverride?.TotalSeconds,
            alertTones);
        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private async Task WriteEbsAsync(
        Channel channel,
        EbsService ebs,
        string ffmpegPath,
        Stream output,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var plan = ebs.CreatePlaybackPlan(channel, durationSeconds);
        var args = _ffmpeg.BuildEbsCommand(channel, plan);
        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private static async Task<(bool FadeIn, bool FadeOut)> GetChannelBugCommercialFadesAsync(
        FinTvDbContext db,
        PlayoutItem item,
        double encodeDurationSeconds,
        CancellationToken cancellationToken)
    {
        const double adjacentSeconds = 2.5;
        var previous = await db.PlayoutItems.AsNoTracking()
            .Where(p => p.ChannelId == item.ChannelId && p.Id != item.Id && p.Finish <= item.Start.AddSeconds(adjacentSeconds))
            .OrderByDescending(p => p.Finish)
            .Select(p => new { p.CommercialId, p.Finish })
            .FirstOrDefaultAsync(cancellationToken);
        var next = await db.PlayoutItems.AsNoTracking()
            .Where(p => p.ChannelId == item.ChannelId && p.Id != item.Id && p.Start >= item.Finish.AddSeconds(-adjacentSeconds))
            .OrderBy(p => p.Start)
            .Select(p => new { p.CommercialId, p.Start })
            .FirstOrDefaultAsync(cancellationToken);

        var fadeIn = previous?.CommercialId is not null
            && Math.Abs((item.Start - previous.Finish).TotalSeconds) <= adjacentSeconds
            && (DateTime.UtcNow - item.Start).TotalSeconds < 5;

        var remainingToItemEnd = (item.Finish - DateTime.UtcNow).TotalSeconds;
        var encodeReachesEnd = encodeDurationSeconds >= remainingToItemEnd - 0.75;
        var fadeOut = encodeReachesEnd
            && next?.CommercialId is not null
            && Math.Abs((next.Start - item.Finish).TotalSeconds) <= adjacentSeconds;

        return (fadeIn, fadeOut);
    }

    public async Task<string?> RenderEbsToTempAsync(Guid channelId, double durationSeconds, CancellationToken cancellationToken)
    {
        // Defensive cap: this renders to disk synchronously inside the next resolver request,
        // so an unbounded duration would stall resolution and fill the temp volume.
        durationSeconds = Math.Clamp(durationSeconds, 5, 120);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return null;
        }
        var ebs = scope.ServiceProvider.GetRequiredService<EbsService>();
        var ffmpegPath = _mediaEncoder.EncoderPath;
        var plan = ebs.CreatePlaybackPlan(channel, durationSeconds);
        var args = _ffmpeg.BuildEbsCommand(channel, plan);
        var tempDir = Path.Combine(Path.GetTempPath(), "ChannelFlow", "next-ebs");
        Directory.CreateDirectory(tempDir);
        SweepStaleEbsTempFiles(tempDir);
        var tempFile = Path.Combine(tempDir, $"{channelId:N}_{DateTime.UtcNow.Ticks}.ts");
        try
        {
            await using var stream = File.Create(tempFile);
            await RunFfmpegToStreamAsync(ffmpegPath, args, stream, cancellationToken);
        }
        catch
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            return null;
        }
        return tempFile;
    }

    /// <summary>
    /// Rendered slates are handed to next as one-shot local sources; anything left behind by a
    /// resolver call that was cancelled or crashed would accumulate on the temp volume forever.
    /// </summary>
    private static void SweepStaleEbsTempFiles(string tempDir)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-2);
            foreach (var file in Directory.EnumerateFiles(tempDir, "*.ts"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Best effort; a locked or half-deleted file just gets swept next time.
        }
    }

    private static string? ResolveBugPath(Channel channel, DateTime scheduleUtc, HolidayChannelService holidays)
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

    private async Task RunFfmpegToStreamAsync(string ffmpegPath, IReadOnlyList<string> args, Stream output, CancellationToken cancellationToken)
    {
        // An exit code above 128 means ffmpeg was killed by a signal (135 = SIGBUS,
        // 139 = SIGSEGV, 134 = SIGABRT…): a crash rather than a media error, so give
        // the encoder one quick retry before the caller falls back to the slate.
        const int maxAttempts = 2;
        for (var attempt = 1; ; attempt++)
        {
            var stderr = new StringBuilder();
            var result = await CliWrap.Cli.Wrap(ffmpegPath)
                .WithArguments(args)
                .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output))
                .WithStandardErrorPipe(CliWrap.PipeTarget.ToStringBuilder(stderr))
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteAsync(cancellationToken);

            if (result.ExitCode == 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (result.ExitCode > 128 && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "ffmpeg killed by a signal (exit {ExitCode}); retrying once before falling back",
                    result.ExitCode);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            var error = stderr.ToString().Trim();
            if (error.Length > 2000)
            {
                error = error[^2000..];
            }

            _logger.LogWarning(
                "ffmpeg exited {ExitCode}: {Error}\nffmpeg {Args}",
                result.ExitCode,
                error,
                string.Join(' ', args.Select(static arg => arg.Contains(' ', StringComparison.Ordinal) ? $"\"{arg}\"" : arg)));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"ffmpeg exited {result.ExitCode}"
                    : $"ffmpeg exited {result.ExitCode}: {error}");
        }
    }
}
