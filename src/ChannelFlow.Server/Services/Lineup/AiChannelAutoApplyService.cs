using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Applies default AI channel settings and lineups when channels are added or bulk-updated.
/// </summary>
public class AiChannelAutoApplyService
{
    private static readonly SemaphoreSlim BulkApplyLock = new(1, 1);
    private static readonly object PendingQueueLock = new();
    private static readonly object GenerateAllCancelLock = new();
    private static CancellationTokenSource? _generateAllCts;
    private static int _generateAllWorkerActive;

    private readonly FinTvDbContext _db;
    private readonly AiLineupGeneratorService _generator;
    private readonly LineupGeneratorService _playoutGenerator;
    private readonly PlayoutBuilderService _playoutBuilder;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ChannelCatalogPoolService _pool;
    private readonly ILogger<AiChannelAutoApplyService> _logger;

    public AiChannelAutoApplyService(
        FinTvDbContext db,
        AiLineupGeneratorService generator,
        LineupGeneratorService playoutGenerator,
        PlayoutBuilderService playoutBuilder,
        IServiceScopeFactory scopeFactory,
        ChannelCatalogPoolService pool,
        ILogger<AiChannelAutoApplyService> logger)
    {
        _db = db;
        _generator = generator;
        _playoutGenerator = playoutGenerator;
        _playoutBuilder = playoutBuilder;
        _scopeFactory = scopeFactory;
        _pool = pool;
        _logger = logger;
    }

    public static bool IsEligible(Channel channel)
    {
        if (channel.ContentType is ChannelContentType.Weather or ChannelContentType.News)
        {
            return false;
        }

        var tag = ChannelAiRules.ExtractLibraryTag(channel.FilterJson);
        return !ChannelAiRules.IsExcludedFromAi(tag);
    }

    public async Task ApplyDefaultSettingsAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        var tag = ChannelAiRules.ExtractLibraryTag(channel.FilterJson);
        var rule = ChannelAiRules.GetByLibraryTag(tag);
        if (rule is not null)
        {
            channel.CatalogMode = rule.DefaultCatalogMode;
        }
        else if (!channel.CatalogMode.HasValue)
        {
            channel.CatalogMode = ChannelAiRules.ResolveCatalogMode(channel, tag);
        }

        var templateId = ChannelAiRules.GetDefaultPlayoutTemplateId(tag);
        if (!string.IsNullOrWhiteSpace(templateId))
        {
            channel.AiPlayoutTemplateId = templateId;
        }
        else if (string.IsNullOrWhiteSpace(channel.AiPlayoutTemplateId))
        {
            channel.AiPlayoutTemplateId = AiPlayoutTemplates.NoneId;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AiAutoApplyChannelResult> TryAutoApplyForChannelAsync(
        Guid channelId,
        CancellationToken cancellationToken = default)
    {
        var settings = FinTvRuntime.Current?.Configuration.Ai ?? new AiSettings();
        if (!settings.Enabled || !settings.AutoApplyOnChannelAdd)
        {
            FinTvDebugLog.Ai(_logger, "Auto-apply skipped for {ChannelId}: AI disabled or auto-apply off", channelId);
            return AiAutoApplyChannelResult.Skipped("AI auto-apply is disabled.");
        }

        FinTvDebugLog.Ai(_logger, "Auto-apply starting for channel {ChannelId}", channelId);
        var result = await ApplyChannelLineupAsync(channelId, cancellationToken: cancellationToken);
        if (result.Ok)
        {
            _logger.LogInformation(
                "Auto-applied AI lineup for channel {ChannelName} with 14-day playout rebuild.",
                result.ChannelName);
        }

        return result;
    }

    public async Task<AiAutoApplyChannelResult> ApplyChannelLineupAsync(
        Guid channelId,
        bool rebuildPlayout = true,
        CancellationToken cancellationToken = default)
    {
        Channel? channel;
        using (await ChannelApplyLocks.AcquireAsync(channelId, cancellationToken))
        {
            channel = await _db.Channels.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
            if (channel is null)
            {
                return AiAutoApplyChannelResult.Failed("Channel not found.", channelId);
            }

            if (!IsEligible(channel))
            {
                FinTvDebugLog.Ai(_logger, "Channel {ChannelName} ({ChannelId}) is not eligible for AI lineups", channel.Name, channelId);
                return AiAutoApplyChannelResult.Skipped($"{channel.Name} is not eligible for AI lineups.");
            }

            FinTvDebugLog.Ai(
                _logger,
                "Applying AI lineup to {ChannelName} ({ChannelId}), rebuildPlayout={Rebuild}",
                channel.Name,
                channelId,
                rebuildPlayout);

            try
            {
                await ApplyDefaultSettingsAsync(channelId, cancellationToken);
                _db.ChangeTracker.Clear();

                var preview = await _generator.GenerateAsync(channelId, null, cancellationToken);
                _db.ChangeTracker.Clear();
                FinTvDebugLog.Ai(
                    _logger,
                    "Generated preview for {ChannelName}: {SlotCount} slots",
                    channel.Name,
                    preview.LineupSlots.Count);

                await _generator.ApplyAsync(
                    channelId,
                    preview.LineupSlots,
                    rebuildPlayout: false,
                    _playoutGenerator,
                    cancellationToken,
                    preview.WeeklyLineups);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("AI lineup apply cancelled for channel {ChannelId}", channelId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI lineup apply failed for channel {ChannelId}", channelId);
                return AiAutoApplyChannelResult.Failed(ex.Message, channelId, channel.Name);
            }
        }

        if (!rebuildPlayout)
        {
            return AiAutoApplyChannelResult.Succeeded(channelId, channel.Name);
        }

        try
        {
            await _playoutBuilder.RebuildChannelAndTrackAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
            return AiAutoApplyChannelResult.Succeeded(channelId, channel.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI lineup saved for channel {ChannelId} but the Live TV guide was not rebuilt", channelId);
            return AiAutoApplyChannelResult.Failed(ex.Message, channelId, channel.Name);
        }
    }

    /// <summary>
    /// Generates the weekly AI lineup once, then fills missing playout days through the horizon.
    /// When 14 days already exist, playout is left alone; the next day is appended at local midnight.
    /// </summary>
    public async Task<AiAutoApplyChannelResult> GenerateAndBuildHorizonDaysAsync(
        Guid channelId,
        AiProvider? providerOverride,
        Action<AiChannelBuildProgress>? onProgress,
        CancellationToken cancellationToken = default,
        bool skipIfAtHorizon = false,
        bool buildPlayout = true)
    {
        var daysToBuild = PlayoutScheduleHelper.GetPlayoutDaysToBuild();
        Channel? channel;
        AiLineupPreviewResult? preview = null;

        using (await ChannelApplyLocks.AcquireAsync(channelId, cancellationToken))
        {
            channel = await _db.Channels.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
            if (channel is null)
            {
                return AiAutoApplyChannelResult.Failed("Channel not found.", channelId);
            }

            if (!IsEligible(channel))
            {
                return AiAutoApplyChannelResult.Skipped($"{channel.Name} is not eligible for AI lineups.");
            }

            var coverage = await GetHorizonDayCoverageAsync(channelId, daysToBuild, cancellationToken)
                .ConfigureAwait(false);
            if (skipIfAtHorizon && ShouldLeaveHorizonForMidnight(coverage))
            {
                ReportProgress(onProgress, new AiChannelBuildProgress
                {
                    CurrentDay = daysToBuild,
                    TotalDays = daysToBuild,
                    Phase = "horizon-full",
                    ChannelName = channel.Name
                });
                return AiAutoApplyChannelResult.Skipped(
                    $"{channel.Name} already has a {daysToBuild}-day guide. The next day is built at midnight.");
            }

            ReportProgress(onProgress, new AiChannelBuildProgress
            {
                CurrentDay = 1,
                TotalDays = daysToBuild,
                Phase = "generating",
                ChannelName = channel.Name
            });

            try
            {
                await ApplyDefaultSettingsAsync(channelId, cancellationToken);
                _db.ChangeTracker.Clear();

                var liveChannel = await _db.Channels.FirstAsync(c => c.Id == channelId, cancellationToken);
                ReportProgress(onProgress, new AiChannelBuildProgress
                {
                    CurrentDay = 1,
                    TotalDays = daysToBuild,
                    Phase = "priming",
                    ChannelName = liveChannel.Name
                });
                await _pool.EnsurePrimedAsync(
                    liveChannel,
                    batchChannels: null,
                    onPage: (name, page, total) =>
                    {
                        ReportProgress(onProgress, new AiChannelBuildProgress
                        {
                            CurrentDay = page,
                            TotalDays = total,
                            Phase = $"priming page {page}/{total}",
                            ChannelName = name
                        });
                    },
                    cancellationToken);
                _db.ChangeTracker.Clear();

                preview = await _generator.GenerateAsync(channelId, providerOverride, cancellationToken);
                _db.ChangeTracker.Clear();
                await _generator.ApplyAsync(
                    channelId,
                    preview.LineupSlots,
                    rebuildPlayout: false,
                    _playoutGenerator,
                    cancellationToken,
                    preview.WeeklyLineups);
                _db.ChangeTracker.Clear();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("AI lineup generate cancelled for channel {ChannelId}", channelId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI lineup generate failed for channel {ChannelId}", channelId);
                return AiAutoApplyChannelResult.Failed(ex.Message, channelId, channel.Name);
            }

            if (!buildPlayout)
            {
                ReportProgress(onProgress, new AiChannelBuildProgress
                {
                    CurrentDay = 1,
                    TotalDays = daysToBuild,
                    Phase = "lineup",
                    ChannelName = channel.Name,
                    Preview = preview
                });
                return AiAutoApplyChannelResult.Succeeded(channelId, channel.Name, preview);
            }

            coverage = await GetHorizonDayCoverageAsync(channelId, daysToBuild, cancellationToken)
                .ConfigureAwait(false);
            if (skipIfAtHorizon && ShouldLeaveHorizonForMidnight(coverage))
            {
                ReportProgress(onProgress, new AiChannelBuildProgress
                {
                    CurrentDay = daysToBuild,
                    TotalDays = daysToBuild,
                    Phase = "horizon-full",
                    ChannelName = channel.Name,
                    Preview = preview
                });
                _logger.LogInformation(
                    "{Channel} already has a {Days}-day guide; skipping playout rebuild. Next day will be built at midnight.",
                    channel.Name,
                    daysToBuild);
                return AiAutoApplyChannelResult.Succeeded(channelId, channel.Name, preview, playoutAlreadyAtHorizon: true);
            }

            for (var day = 0; day < daysToBuild; day++)
            {
                if (skipIfAtHorizon && coverage[day])
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(onProgress, new AiChannelBuildProgress
                {
                    CurrentDay = day + 1,
                    TotalDays = daysToBuild,
                    Phase = "playout",
                    ChannelName = channel.Name,
                    Preview = preview
                });

                try
                {
                    _db.ChangeTracker.Clear();
                    await BuildChannelPlayoutDayAsync(
                        channelId,
                        day,
                        cancellationToken,
                        interruptStream: day == 0).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("AI playout build cancelled for channel {ChannelId}", channelId);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "AI lineup saved for {Channel} but playout day {Day}/{Days} failed",
                        channel.Name,
                        day + 1,
                        daysToBuild);
                    return AiAutoApplyChannelResult.Failed(
                        $"Playout day {day + 1} failed: {ex.Message}",
                        channelId,
                        channel.Name,
                        preview);
                }
            }
        }

        return AiAutoApplyChannelResult.Succeeded(channelId, channel.Name, preview);
    }

    private static void ReportProgress(Action<AiChannelBuildProgress>? onProgress, AiChannelBuildProgress progress)
        => onProgress?.Invoke(progress);

    public bool IsGenerateAllJobRunning
    {
        get
        {
            RefreshGenerateAllJobState();
            return FinTvRuntime.Current?.Configuration.AiGenerateAllJob.IsRunning == true;
        }
    }

    private static bool IsGenerateAllWorkerActive => Volatile.Read(ref _generateAllWorkerActive) > 0;

    /// <summary>
    /// Clears a persisted running flag when the background worker is no longer active.
    /// </summary>
    public void RefreshGenerateAllJobState()
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return;
        }

        var job = plugin.Configuration.AiGenerateAllJob;
        if (!job.IsRunning)
        {
            return;
        }

        if (!IsGenerateAllWorkerActive)
        {
            job.IsRunning = false;
            job.WasStale = true;
            job.CompletedAt = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(job.LastError))
            {
                job.LastError = "Generate all stopped unexpectedly (server restart or background task ended).";
            }

            plugin.SaveConfiguration();
            return;
        }

        if (job.LastProgressAt.HasValue
            && DateTime.UtcNow - job.LastProgressAt.Value > TimeSpan.FromMinutes(20))
        {
            lock (GenerateAllCancelLock)
            {
                _generateAllCts?.Cancel();
            }
        }
    }

    public object BuildGenerateAllStatus()
    {
        RefreshGenerateAllJobState();
        var job = FinTvRuntime.Current?.Configuration.AiGenerateAllJob ?? new AiGenerateAllJobState();
        return new
        {
            isRunning = job.IsRunning,
            workerActive = IsGenerateAllWorkerActive,
            totalDays = job.TotalDays,
            totalChannels = job.TotalChannels,
            totalSteps = job.TotalSteps,
            completedSteps = job.CompletedSteps,
            currentDay = job.CurrentDay,
            currentPhase = job.CurrentPhase,
            currentChannelName = job.CurrentChannelName,
            lineupsGenerated = job.LineupsGenerated,
            lineupsFailed = job.LineupsFailed,
            playoutDaysBuilt = job.PlayoutDaysBuilt,
            playoutDaysFailed = job.PlayoutDaysFailed,
            lastError = job.LastError,
            startedAt = job.StartedAt,
            completedAt = job.CompletedAt,
            lastProgressAt = job.LastProgressAt,
            wasCancelled = job.WasCancelled,
            wasStale = job.WasStale
        };
    }

    /// <summary>
    /// Requests cancellation of the in-flight staggered generate-all job.
    /// </summary>
    /// <returns>True when a running job received a cancel signal.</returns>
    public bool CancelGenerateAll()
    {
        RefreshGenerateAllJobState();

        var plugin = FinTvRuntime.Current;
        var job = plugin?.Configuration.AiGenerateAllJob;
        if (job is null || !job.IsRunning)
        {
            return false;
        }

        lock (GenerateAllCancelLock)
        {
            if (_generateAllCts is not null && !_generateAllCts.IsCancellationRequested)
            {
                _generateAllCts.Cancel();
                return true;
            }
        }

        if (!IsGenerateAllWorkerActive)
        {
            ClearStuckGenerateAllJob("Cancelled by user.");
            return true;
        }

        return _generateAllCts?.IsCancellationRequested == true;
    }

    private static void ClearStuckGenerateAllJob(string message)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return;
        }

        var job = plugin.Configuration.AiGenerateAllJob;
        job.IsRunning = false;
        job.WasCancelled = true;
        job.WasStale = !IsGenerateAllWorkerActive;
        job.CompletedAt = DateTime.UtcNow;
        job.LastError = message;
        plugin.SaveConfiguration();
    }

    private CancellationToken BeginGenerateAllCancellation()
    {
        lock (GenerateAllCancelLock)
        {
            _generateAllCts?.Cancel();
            _generateAllCts?.Dispose();
            _generateAllCts = new CancellationTokenSource();
            return _generateAllCts.Token;
        }
    }

    private static void EndGenerateAllCancellation()
    {
        lock (GenerateAllCancelLock)
        {
            _generateAllCts?.Dispose();
            _generateAllCts = null;
        }
    }

    /// <summary>
    /// Generates AI lineups and playout one channel at a time:
    /// generate the weekly lineup, build day 1, then day 2, through 14 days, then the next channel.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunStaggeredGenerateAllAsync(CancellationToken cancellationToken = default)
    {
        if (FinTvRuntime.Current?.Configuration.Ai.Enabled != true)
        {
            throw new InvalidOperationException("AI lineup generation is disabled.");
        }

        await BulkApplyLock.WaitAsync(cancellationToken);
        var state = new AiGenerateAllJobState
        {
            IsRunning = true,
            StartedAt = DateTime.UtcNow,
            LastError = null,
            CompletedAt = null,
            WasCancelled = false,
            WasStale = false,
            LastProgressAt = DateTime.UtcNow
        };

        try
        {
            var daysToBuild = PlayoutScheduleHelper.GetPlayoutDaysToBuild();
            var channelRows = await _db.Channels
                .AsNoTracking()
                .Where(c => c.Enabled && c.ContentType != ChannelContentType.Weather && c.ContentType != ChannelContentType.News)
                .OrderBy(c => c.Number)
                .Select(c => new { c.Id, c.Name, c.FilterJson, c.ContentType })
                .ToListAsync(cancellationToken);

            var eligibleChannels = channelRows
                .Where(c => IsEligible(new Channel { FilterJson = c.FilterJson, ContentType = c.ContentType }))
                .Select(c => (c.Id, c.Name))
                .ToList();

            FinTvDebugLog.Ai(
                _logger,
                "Generate-all starting: {Eligible}/{Total} eligible channels, {Days} days, {Steps} steps",
                eligibleChannels.Count,
                channelRows.Count,
                daysToBuild,
                eligibleChannels.Count * daysToBuild);

            state.TotalDays = daysToBuild;
            state.TotalChannels = eligibleChannels.Count;
            state.TotalSteps = eligibleChannels.Count * daysToBuild;
            SaveGenerateAllState(state);

            if (eligibleChannels.Count == 0)
            {
                state.LastError = "No eligible channels found for AI lineups.";
                return;
            }

            var channelIndex = 0;
            foreach (var (channelId, channelName) in eligibleChannels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                state.CurrentChannelName = channelName;
                state.CurrentDay = 1;
                state.CurrentPhase = "generating";
                SaveGenerateAllState(state);

                try
                {
                    using var lineupScope = _scopeFactory.CreateScope();
                    var lineupService = lineupScope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                    var lineupResult = await lineupService.GenerateAndBuildHorizonDaysAsync(
                        channelId,
                        providerOverride: null,
                        onProgress: progress =>
                        {
                            state.CurrentChannelName = progress.ChannelName;
                            state.CurrentDay = progress.CurrentDay;
                            state.CurrentPhase = progress.Phase;
                            state.CompletedSteps = (channelIndex * daysToBuild)
                                + (progress.Phase == "generating" || progress.Phase == "horizon-full"
                                    ? 0
                                    : Math.Max(0, progress.CurrentDay - 1));
                            SaveGenerateAllState(state);
                        },
                        cancellationToken,
                        skipIfAtHorizon: true).ConfigureAwait(false);

                    if (lineupResult.Ok)
                    {
                        state.LineupsGenerated++;
                        if (!lineupResult.PlayoutAlreadyAtHorizon)
                        {
                            state.PlayoutDaysBuilt += daysToBuild;
                        }

                        _logger.LogInformation(
                            "AI generate-all finished channel {ChannelName}: lineup{Playout}.",
                            channelName,
                            lineupResult.PlayoutAlreadyAtHorizon
                                ? "; 14-day guide already filled (next day at midnight)"
                                : $" plus {daysToBuild} playout days");
                    }
                    else if (!lineupResult.WasSkipped)
                    {
                        state.LineupsFailed++;
                        state.LastError = $"{channelName}: {lineupResult.Error}";
                        _logger.LogWarning(
                            "AI lineup generation failed for {ChannelName}: {Error}",
                            channelName,
                            lineupResult.Error);
                    }
                    else
                    {
                        FinTvDebugLog.Ai(
                            _logger,
                            "Generate-all skipped ineligible channel {ChannelName}: {Reason}",
                            channelName,
                            lineupResult.Error ?? "(none)");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    state.LineupsFailed++;
                    state.PlayoutDaysFailed += daysToBuild;
                    state.LastError = $"{channelName}: {ex.Message}";
                    _logger.LogWarning(ex, "AI generate-all failed for {ChannelName}", channelName);
                }

                channelIndex++;
                state.CompletedSteps = channelIndex * daysToBuild;
                SaveGenerateAllState(state);
            }

            _logger.LogInformation(
                "AI generate-all finished: {Lineups} lineups, {PlayoutDays} playout days built across {Channels} channels and {Days} days.",
                state.LineupsGenerated,
                state.PlayoutDaysBuilt,
                eligibleChannels.Count,
                daysToBuild);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.WasCancelled = true;
            _logger.LogInformation("Staggered AI generate-all cancelled.");
            throw;
        }
        finally
        {
            state.IsRunning = false;
            state.CurrentChannelName = null;
            state.CurrentPhase = null;
            state.CompletedAt = DateTime.UtcNow;
            SaveGenerateAllState(state);
            BulkApplyLock.Release();
        }
    }

    public async Task ClearChannelPlayoutFromTodayAsync(
        Guid channelId,
        CancellationToken cancellationToken = default)
    {
        var start = PlayoutScheduleHelper.GetScheduleDayStartUtc(DateTime.UtcNow);
        var existing = await _db.PlayoutItems
            .Where(p => p.ChannelId == channelId && p.Finish > start)
            .ToListAsync(cancellationToken);
        if (existing.Count == 0)
        {
            FinTvDebugLog.Ai(_logger, "No future playout to clear for channel {ChannelId}", channelId);
            return;
        }

        FinTvDebugLog.Ai(_logger, "Clearing {Count} future playout items for channel {ChannelId}", existing.Count, channelId);
        _db.PlayoutItems.RemoveRange(existing);
        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();
    }

    public async Task BuildChannelPlayoutDayAsync(
        Guid channelId,
        int dayOffset,
        CancellationToken cancellationToken = default,
        bool interruptStream = true)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        var dayStart = PlayoutScheduleHelper.GetScheduleDayStartUtc(DateTime.UtcNow, dayOffset);
        var dayEnd = PlayoutScheduleHelper.GetScheduleDayStartUtc(DateTime.UtcNow, dayOffset + 1);
        FinTvDebugLog.Ai(
            _logger,
            "Building playout day {DayOffset} for {ChannelName}: {Start:u} to {End:u}",
            dayOffset,
            channel.Name,
            dayStart,
            dayEnd);

        await _playoutGenerator.BuildPlayoutAsync(
            channel,
            dayStart,
            dayEnd,
            PlayoutBuildMode.ReplaceWindow,
            cancellationToken,
            interruptStream);

        var itemCount = await _db.PlayoutItems.CountAsync(
            p => p.ChannelId == channelId && p.Start >= dayStart && p.Start < dayEnd,
            cancellationToken);
        FinTvDebugLog.Ai(
            _logger,
            "Playout day {DayOffset} for {ChannelName}: {ItemCount} items in window",
            dayOffset,
            channel.Name,
            itemCount);
    }

    /// <summary>
    /// Extends playout by one calendar day when yesterday rolled off the 14-day horizon.
    /// </summary>
    public async Task<PlayoutHorizonMaintainResult> MaintainPlayoutHorizonAsync(
        Guid channelId,
        CancellationToken cancellationToken = default)
    {
        using var gate = await ChannelApplyLocks.AcquireAsync(channelId, cancellationToken);
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null || channel.ContentType is ChannelContentType.Weather or ChannelContentType.News || !IsEligible(channel))
        {
            return PlayoutHorizonMaintainResult.NeedsFullBuild;
        }

        if (!await HasDefaultLineupAsync(channelId, cancellationToken).ConfigureAwait(false))
        {
            return PlayoutHorizonMaintainResult.NeedsFullBuild;
        }

        var now = DateTime.UtcNow;
        var daysToBuild = PlayoutScheduleHelper.GetPlayoutDaysToBuild();
        var horizonEnd = PlayoutScheduleHelper.GetHorizonEndUtc(now);
        var latestFinish = await GetLatestPlayoutFinishUtcAsync(channelId, now, cancellationToken)
            .ConfigureAwait(false);
        FinTvDebugLog.Ai(
            _logger,
            "Horizon check for {ChannelName}: latestFinish={LatestFinish}, horizonEnd={HorizonEnd:u}",
            channel.Name,
            latestFinish?.ToString("u") ?? "none",
            horizonEnd);

        if (latestFinish is null || latestFinish.Value <= now)
        {
            return PlayoutHorizonMaintainResult.NeedsFullBuild;
        }

        if (latestFinish.Value >= horizonEnd)
        {
            return PlayoutHorizonMaintainResult.AlreadyAtHorizon;
        }

        var nextDayOffset = daysToBuild - 1;
        _db.ChangeTracker.Clear();
        await BuildChannelPlayoutDayAsync(
            channelId,
            nextDayOffset,
            cancellationToken,
            interruptStream: false).ConfigureAwait(false);
        return PlayoutHorizonMaintainResult.ExtendedOneDay;
    }

    public async Task<bool> TryExtendPlayoutHorizonAsync(
        Guid channelId,
        CancellationToken cancellationToken = default)
    {
        var result = await MaintainPlayoutHorizonAsync(channelId, cancellationToken).ConfigureAwait(false);
        return result is PlayoutHorizonMaintainResult.AlreadyAtHorizon
            or PlayoutHorizonMaintainResult.ExtendedOneDay;
    }

    /// <summary>
    /// Extends eligible AI channels that are one day short of the configured playout horizon.
    /// </summary>
    public async Task<int> MaintainEligiblePlayoutHorizonsAsync(CancellationToken cancellationToken = default)
    {
        if (FinTvRuntime.Current?.Configuration.Ai.Enabled != true)
        {
            return 0;
        }

        var channels = await _db.Channels
            .AsNoTracking()
            .Where(c => c.Enabled && c.ContentType != ChannelContentType.Weather && c.ContentType != ChannelContentType.News)
            .OrderBy(c => c.Number)
            .Select(c => new { c.Id, c.Name, c.FilterJson, c.ContentType })
            .ToListAsync(cancellationToken);

        var extended = 0;
        foreach (var channel in channels)
        {
            if (!IsEligible(new Channel { FilterJson = channel.FilterJson, ContentType = channel.ContentType }))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var maintainResult = await MaintainPlayoutHorizonAsync(channel.Id, cancellationToken)
                .ConfigureAwait(false);
            if (maintainResult != PlayoutHorizonMaintainResult.ExtendedOneDay)
            {
                continue;
            }

            extended++;
            _logger.LogInformation(
                "Extended AI channel {ChannelName} playout by one day (horizon maintenance).",
                channel.Name);
        }

        return extended;
    }

    private async Task<bool[]> GetHorizonDayCoverageAsync(
        Guid channelId,
        int daysToBuild,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var coverage = new bool[daysToBuild];
        for (var day = 0; day < daysToBuild; day++)
        {
            var start = PlayoutScheduleHelper.GetScheduleDayStartUtc(now, day);
            var end = PlayoutScheduleHelper.GetScheduleDayStartUtc(now, day + 1);
            coverage[day] = await _db.PlayoutItems
                .AsNoTracking()
                .AnyAsync(
                    p => p.ChannelId == channelId && p.Finish > start && p.Start < end,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return coverage;
    }

    private static bool ShouldLeaveHorizonForMidnight(IReadOnlyList<bool> coverage)
    {
        if (coverage.Count == 0)
        {
            return false;
        }

        var missing = 0;
        var lastMissing = false;
        for (var i = 0; i < coverage.Count; i++)
        {
            if (coverage[i])
            {
                continue;
            }

            missing++;
            lastMissing = i == coverage.Count - 1;
        }

        return missing == 0 || (missing == 1 && lastMissing);
    }

    private async Task<DateTime?> GetLatestPlayoutFinishUtcAsync(
        Guid channelId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        return await _db.PlayoutItems
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId && p.Finish > nowUtc)
            .Select(p => (DateTime?)p.Finish)
            .MaxAsync(cancellationToken);
    }

    private async Task<bool> HasDefaultLineupAsync(Guid channelId, CancellationToken cancellationToken)
    {
        return await _db.Lineups
            .AsNoTracking()
            .Where(l => l.ChannelId == channelId && l.IsDefault)
            .AnyAsync(l => l.Slots.Any(s => s.Candidates.Count > 0), cancellationToken);
    }

    private static void SaveGenerateAllState(AiGenerateAllJobState state)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return;
        }

        plugin.Configuration.AiGenerateAllJob = state;
        state.LastProgressAt = DateTime.UtcNow;
        plugin.SaveConfiguration();
    }

    /// <summary>
    /// Runs AI auto-apply in the background so channel create/preset APIs return immediately.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    public void QueueAutoApplyForChannel(Guid channelId)
    {
        var settings = FinTvRuntime.Current?.Configuration.Ai ?? new AiSettings();
        if (!settings.Enabled || !settings.AutoApplyOnChannelAdd)
        {
            return;
        }

        EnqueueAutoApplyChannel(channelId);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                await service.ProcessPendingAutoApplyQueueAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background AI auto-apply queue processing failed.");
            }
        });
    }

    /// <summary>
    /// Processes channels queued for AI auto-apply (lineup + 14-day playout rebuild).
    /// </summary>
    /// <param name="progress">Optional progress reporter for scheduled tasks.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of channels processed.</returns>
    public async Task<int> ProcessPendingAutoApplyQueueAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (FinTvRuntime.Current?.Configuration.Ai.Enabled != true
            || !FinTvRuntime.Current.Configuration.Ai.AutoApplyOnChannelAdd)
        {
            progress?.Report(100);
            return 0;
        }

        List<Guid> pending;
        lock (PendingQueueLock)
        {
            pending = FinTvRuntime.Current.Configuration.AiPendingAutoApplyChannelIds
                .Distinct()
                .ToList();
        }

        if (pending.Count == 0)
        {
            progress?.Report(100);
            return 0;
        }

        var processed = 0;
        for (var index = 0; index < pending.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channelId = pending[index];
            var result = await ApplyChannelLineupAsync(channelId, cancellationToken: cancellationToken).ConfigureAwait(false);
            RemoveFromPendingQueue(channelId);
            processed++;

            if (result.Ok)
            {
                _logger.LogInformation(
                    "Auto-applied AI lineup for channel {ChannelName} with 14-day playout rebuild.",
                    result.ChannelName);
            }
            else if (!result.WasSkipped)
            {
                _logger.LogWarning(
                    "AI lineup auto-apply failed for channel {ChannelId}: {Error}",
                    channelId,
                    result.Error);
            }

            progress?.Report((index + 1) * 100d / pending.Count);
        }

        return processed;
    }

    private static void EnqueueAutoApplyChannel(Guid channelId)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return;
        }

        lock (PendingQueueLock)
        {
            if (!plugin.Configuration.AiPendingAutoApplyChannelIds.Contains(channelId))
            {
                plugin.Configuration.AiPendingAutoApplyChannelIds.Add(channelId);
                plugin.SaveConfiguration();
            }
        }
    }

    private static void RemoveFromPendingQueue(Guid channelId)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return;
        }

        lock (PendingQueueLock)
        {
            if (plugin.Configuration.AiPendingAutoApplyChannelIds.Remove(channelId))
            {
                plugin.SaveConfiguration();
            }
        }
    }

    /// <summary>
    /// Applies AI lineups to all eligible channels in the background after settings are saved.
    /// </summary>
    public void QueueApplyToAllEligibleChannels()
    {
        var settings = FinTvRuntime.Current?.Configuration.Ai ?? new AiSettings();
        if (!settings.Enabled || !settings.AutoApplyToAllChannelsOnSave)
        {
            return;
        }

        QueueApplyToAllEligibleChannelsInBackground("apply-to-all");
    }

    /// <summary>
    /// Queues AI lineup generation for all eligible channels (admin Generate All button).
    /// Returns immediately so reverse proxies do not time out on long LLM/playout runs.
    /// </summary>
    public void QueueManualGenerateAllEligibleChannels()
    {
        if (FinTvRuntime.Current?.Configuration.Ai.Enabled != true || IsGenerateAllJobRunning)
        {
            return;
        }

        QueueStaggeredGenerateAllInBackground("generate-all");
    }

    private void QueueStaggeredGenerateAllInBackground(string operationName)
    {
        _logger.LogInformation("Queueing staggered AI {Operation} for eligible channels.", operationName);
        var cancellationToken = BeginGenerateAllCancellation();
        Interlocked.Increment(ref _generateAllWorkerActive);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                await service.RunStaggeredGenerateAllAsync(cancellationToken).ConfigureAwait(false);
                var job = FinTvRuntime.Current?.Configuration.AiGenerateAllJob;
                if (job is not null && !job.WasCancelled)
                {
                    _logger.LogInformation(
                        "Background staggered AI {Operation} finished: {Lineups} lineups, {PlayoutDays} playout days, {FailedLineups} lineup failures, {FailedDays} day failures.",
                        operationName,
                        job.LineupsGenerated,
                        job.PlayoutDaysBuilt,
                        job.LineupsFailed,
                        job.PlayoutDaysFailed);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Background staggered AI {Operation} cancelled.", operationName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background staggered AI {Operation} failed.", operationName);
                var plugin = FinTvRuntime.Current;
                if (plugin is not null)
                {
                    var job = plugin.Configuration.AiGenerateAllJob;
                    job.IsRunning = false;
                    job.LastError = ex.Message;
                    job.CompletedAt = DateTime.UtcNow;
                    plugin.SaveConfiguration();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _generateAllWorkerActive);
                EndGenerateAllCancellation();
            }
        });
    }

    private void QueueApplyToAllEligibleChannelsInBackground(string operationName)
    {
        QueueStaggeredGenerateAllInBackground(operationName);
    }

    public async Task<IReadOnlyList<AiAutoApplyChannelResult>> ApplyToAllEligibleChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        if (FinTvRuntime.Current?.Configuration.Ai.Enabled != true)
        {
            throw new InvalidOperationException("AI lineup generation is disabled.");
        }

        await BulkApplyLock.WaitAsync(cancellationToken);
        try
        {
            var channels = await _db.Channels
                .AsNoTracking()
                .Where(c => c.Enabled && c.ContentType != ChannelContentType.Weather && c.ContentType != ChannelContentType.News)
                .OrderBy(c => c.Number)
                .Select(c => new { c.Id, c.Name, c.FilterJson, c.ContentType })
                .ToListAsync(cancellationToken);

            var results = new List<AiAutoApplyChannelResult>();
            foreach (var channel in channels)
            {
                if (!IsEligible(new Channel { FilterJson = channel.FilterJson, ContentType = channel.ContentType }))
                {
                    results.Add(AiAutoApplyChannelResult.Skipped($"{channel.Name} is not eligible for AI lineups."));
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                results.Add(await service.ApplyChannelLineupAsync(channel.Id, cancellationToken: cancellationToken));
            }

            return results;
        }
        finally
        {
            BulkApplyLock.Release();
        }
    }
}

public enum PlayoutHorizonMaintainResult
{
    NeedsFullBuild,
    AlreadyAtHorizon,
    ExtendedOneDay
}

public class AiAutoApplyChannelResult
{
    public Guid? ChannelId { get; set; }

    public string? ChannelName { get; set; }

    public bool Ok { get; set; }

    public bool WasSkipped { get; set; }

    public string? Error { get; set; }

    public AiLineupPreviewResult? Preview { get; set; }

    public bool PlayoutAlreadyAtHorizon { get; set; }

    public static AiAutoApplyChannelResult Succeeded(
        Guid channelId,
        string channelName,
        AiLineupPreviewResult? preview = null,
        bool playoutAlreadyAtHorizon = false)
        => new()
        {
            Ok = true,
            ChannelId = channelId,
            ChannelName = channelName,
            Preview = preview,
            PlayoutAlreadyAtHorizon = playoutAlreadyAtHorizon
        };

    public static AiAutoApplyChannelResult Skipped(string reason)
        => new() { WasSkipped = true, Error = reason };

    public static AiAutoApplyChannelResult Failed(
        string error,
        Guid? channelId = null,
        string? channelName = null,
        AiLineupPreviewResult? preview = null)
        => new() { Ok = false, Error = error, ChannelId = channelId, ChannelName = channelName, Preview = preview };
}

public sealed class AiChannelBuildProgress
{
    public int CurrentDay { get; init; }

    public int TotalDays { get; init; }

    public string Phase { get; init; } = string.Empty;

    public string ChannelName { get; init; } = string.Empty;

    public AiLineupPreviewResult? Preview { get; init; }
}
