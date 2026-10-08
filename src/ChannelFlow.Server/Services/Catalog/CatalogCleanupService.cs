using FinTv;
using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

/// <summary>
/// Marks catalog rows missing when Jellyfin no longer reports them, then deletes after a grace period.
/// </summary>
public sealed class CatalogCleanupService
{
    private const int ScanPageSize = 500;
    private const int FlagChunkSize = 400;

    private readonly FinTvDbContext _db;
    private readonly PathRemapService _remap;
    private readonly ChannelCatalogPoolService _pool;
    private readonly ILogger<CatalogCleanupService> _logger;

    public CatalogCleanupService(
        FinTvDbContext db,
        PathRemapService remap,
        ChannelCatalogPoolService pool,
        ILogger<CatalogCleanupService> logger)
    {
        _db = db;
        _remap = remap;
        _pool = pool;
        _logger = logger;
    }

    public static int ClampGracePeriodDays(int days) => Math.Clamp(days, 0, 90);

    /// <summary>
    /// Clears running flags left behind by a run that died with the process. No cleanup run can
    /// survive a restart, so a persisted flag found on boot is always stale; without this every
    /// later run short-circuits on "already running" and the UI shows a permanent 0-of progress.
    /// </summary>
    /// <returns>True when an interrupted run was found and cleared.</returns>
    public static bool ResetInterruptedState()
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return false;
        }

        var settings = plugin.Configuration.CatalogCleanup;
        if (!settings.TaskState.IsRunning && !settings.LocalScan.IsRunning)
        {
            return false;
        }

        settings.TaskState.IsRunning = false;
        settings.LocalScan.IsRunning = false;
        plugin.SaveConfiguration();
        return true;
    }

    public async Task<CatalogCleanupStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        var state = settings.TaskState;
        var scan = settings.LocalScan;
        return new CatalogCleanupStatus(
            settings.GracePeriodDays,
            state.IsRunning,
            state.MarkedMissing,
            state.Removed,
            state.LastError,
            state.LastStartedAt,
            state.LastCompletedAt,
            settings.LastCatalogSyncStartedAt,
            settings.LastCatalogSyncCompletedAt,
            await CountMissingAsync(cancellationToken),
            scan.IsRunning,
            scan.TotalItems,
            scan.ProcessedItems,
            scan.Found,
            scan.MarkedMissing,
            scan.Restored,
            scan.Skipped,
            scan.LastError,
            scan.LastStartedAt,
            scan.LastCompletedAt);
    }

    public void BeginCatalogSync()
    {
        var plugin = FinTvRuntime.Current ?? throw new InvalidOperationException("ChannelFlow is not initialized.");
        plugin.Configuration.CatalogCleanup.LastCatalogSyncStartedAt = DateTime.UtcNow;
        plugin.SaveConfiguration();
    }

    public async Task<int> CompleteCatalogSyncAsync(CancellationToken cancellationToken)
    {
        var plugin = FinTvRuntime.Current ?? throw new InvalidOperationException("ChannelFlow is not initialized.");
        var settings = plugin.Configuration.CatalogCleanup;
        var cutoff = settings.LastCatalogSyncStartedAt;
        if (cutoff is null || cutoff < DateTime.UtcNow.AddHours(-12))
        {
            cutoff = await ResolveFallbackCutoffAsync(cancellationToken);
        }

        var marked = cutoff is DateTime started
            ? await MarkMissingNotSeenSinceAsync(started, cancellationToken)
            : 0;

        settings.LastCatalogSyncCompletedAt = DateTime.UtcNow;
        plugin.SaveConfiguration();
        return marked;
    }

    public async Task<int> MarkMissingExceptAsync(IReadOnlySet<Guid> presentIds, CancellationToken cancellationToken)
        => await MarkMissingExceptAsync(presentIds, connectionId: null, cancellationToken);

    public async Task<int> MarkMissingExceptAsync(
        IReadOnlySet<Guid> presentIds,
        Guid? connectionId,
        CancellationToken cancellationToken)
    {
        await CatalogSchema.EnsureEpisodesTableAsync(_db, cancellationToken);
        var now = DateTime.UtcNow;
        var marked = await Scope(_db.MediaItems, presentIds, connectionId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await Scope(_db.TvShows, presentIds, connectionId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await Scope(_db.Episodes, presentIds, connectionId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await Scope(_db.Movies, presentIds, connectionId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await Scope(_db.Music, presentIds, connectionId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await Scope(_db.MusicVideos, presentIds, connectionId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await Scope(_db.PastTenseNews, presentIds, connectionId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        await _pool.PruneMissingAsync(cancellationToken);
        return marked;
    }

    private static IQueryable<MediaItem> Scope(
        IQueryable<MediaItem> query,
        IReadOnlySet<Guid> presentIds,
        Guid? connectionId)
    {
        query = query.Where(row => !row.IsMissing && !presentIds.Contains(row.Id));
        if (connectionId is Guid cid)
        {
            query = query.Where(row => row.SourceConnectionId == cid || row.SourceConnectionId == null);
        }

        return query;
    }

    private static IQueryable<T> Scope<T>(
        IQueryable<T> query,
        IReadOnlySet<Guid> presentIds,
        Guid? connectionId)
        where T : CatalogMediaRow
    {
        query = query.Where(row => !row.IsMissing && !presentIds.Contains(row.Id));
        if (connectionId is Guid cid)
        {
            query = query.Where(row => row.SourceConnectionId == cid || row.SourceConnectionId == null);
        }

        return query;
    }

    public async Task<CatalogCleanupRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var plugin = FinTvRuntime.Current ?? throw new InvalidOperationException("ChannelFlow is not initialized.");
        var settings = plugin.Configuration.CatalogCleanup;
        var state = settings.TaskState;
        if (state.IsRunning)
        {
            return new CatalogCleanupRunResult(0, 0, await CountMissingAsync(cancellationToken), AlreadyRunning: true);
        }

        state.IsRunning = true;
        state.LastError = null;
        state.LastStartedAt = DateTime.UtcNow;
        state.MarkedMissing = 0;
        state.Removed = 0;

        // Everything after the flag flip lives inside the try so a failed persist still falls
        // through to the finally below; otherwise the flag would strand as running until the
        // next restart.
        try
        {
            plugin.SaveConfiguration();
            var cutoff = ResolveMarkCutoff(settings);
            if (cutoff is null)
            {
                cutoff = await ResolveFallbackCutoffAsync(cancellationToken);
            }

            var marked = cutoff is DateTime started
                ? await MarkMissingNotSeenSinceAsync(started, cancellationToken)
                : 0;
            var scan = await ScanLocalFilesAsync(cancellationToken, nested: true);
            var removed = await RemoveExpiredAsync(settings.GracePeriodDays, cancellationToken);
            await _pool.PruneMissingAsync(cancellationToken);
            var stillMissing = await CountMissingAsync(cancellationToken);

            state.MarkedMissing = marked + scan.MarkedMissing;
            state.Removed = removed;
            state.LastCompletedAt = DateTime.UtcNow;
            plugin.SaveConfiguration();

            _logger.LogInformation(
                "Catalog cleanup marked {Marked} missing, restored {Restored} from local files, and removed {Removed} after a {Days}-day grace period.",
                marked + scan.MarkedMissing,
                scan.Restored,
                removed,
                settings.GracePeriodDays);

            return new CatalogCleanupRunResult(marked + scan.MarkedMissing, removed, stillMissing, AlreadyRunning: false);
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
            plugin.SaveConfiguration();
            _logger.LogError(ex, "Catalog cleanup failed");
            throw;
        }
        finally
        {
            state.IsRunning = false;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                // Never let a persist failure strand the in-memory flag as running.
                _logger.LogWarning(ex, "Could not persist catalog cleanup state after the run");
            }
        }
    }

    public async Task<CatalogLocalScanResult> ScanLocalFilesAsync(CancellationToken cancellationToken, bool nested = false)
    {
        var plugin = FinTvRuntime.Current ?? throw new InvalidOperationException("ChannelFlow is not initialized.");
        var settings = plugin.Configuration.CatalogCleanup;
        var scan = settings.LocalScan;
        if (!nested && (scan.IsRunning || settings.TaskState.IsRunning))
        {
            return new CatalogLocalScanResult(0, 0, 0, 0, AlreadyRunning: true);
        }

        scan.IsRunning = true;
        scan.LastError = null;
        scan.LastStartedAt = DateTime.UtcNow;
        scan.ProcessedItems = 0;
        scan.Found = 0;
        scan.MarkedMissing = 0;
        scan.Restored = 0;
        scan.Skipped = 0;

        // Everything after the flag flip lives inside the try so a failed count or persist still
        // falls through to the finally below; otherwise the flag would strand as running until
        // the next restart.
        try
        {
            scan.TotalItems = await _db.MediaItems.CountAsync(cancellationToken);
            plugin.SaveConfiguration();
            var mappings = await _remap.GetAllAsync(cancellationToken);
            var foundIds = new List<Guid>();
            var missingIds = new List<Guid>();
            Guid lastId = Guid.Empty;

            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = await _db.MediaItems.AsNoTracking()
                    .Where(row => row.Id > lastId)
                    .OrderBy(row => row.Id)
                    .Take(ScanPageSize)
                    .Select(row => new { row.Id, row.Path, row.IsMissing, row.SourceConnectionId })
                    .ToListAsync(cancellationToken);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var item in batch)
                {
                    lastId = item.Id;
                    scan.ProcessedItems++;
                    if (string.IsNullOrWhiteSpace(item.Path))
                    {
                        scan.Skipped++;
                        continue;
                    }

                    if (_remap.ExistsAtRemappedPath(item.Path, mappings, item.SourceConnectionId))
                    {
                        scan.Found++;
                        if (item.IsMissing)
                        {
                            foundIds.Add(item.Id);
                        }
                    }
                    else if (!item.IsMissing)
                    {
                        missingIds.Add(item.Id);
                    }
                }

                if (foundIds.Count >= FlagChunkSize)
                {
                    scan.Restored += await ApplyMissingFlagsAsync(foundIds, missing: false, since: null, cancellationToken);
                    foundIds.Clear();
                    plugin.SaveConfiguration();
                }

                if (missingIds.Count >= FlagChunkSize)
                {
                    scan.MarkedMissing += await ApplyMissingFlagsAsync(missingIds, missing: true, since: DateTime.UtcNow, cancellationToken);
                    missingIds.Clear();
                    plugin.SaveConfiguration();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (foundIds.Count > 0)
            {
                scan.Restored += await ApplyMissingFlagsAsync(foundIds, missing: false, since: null, cancellationToken);
            }

            if (missingIds.Count > 0)
            {
                scan.MarkedMissing += await ApplyMissingFlagsAsync(missingIds, missing: true, since: DateTime.UtcNow, cancellationToken);
            }

            scan.LastCompletedAt = DateTime.UtcNow;
            plugin.SaveConfiguration();
            _logger.LogInformation(
                "Local catalog scan found {Found} remapped files, restored {Restored}, marked {Marked} missing, skipped {Skipped}.",
                scan.Found,
                scan.Restored,
                scan.MarkedMissing,
                scan.Skipped);

            return new CatalogLocalScanResult(scan.Found, scan.MarkedMissing, scan.Restored, scan.Skipped, AlreadyRunning: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scan.LastError = ex.Message;
            plugin.SaveConfiguration();
            _logger.LogError(ex, "Local catalog file scan failed");
            throw;
        }
        finally
        {
            scan.IsRunning = false;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                // Never let a persist failure strand the in-memory flag as running.
                _logger.LogWarning(ex, "Could not persist local scan state after the run");
            }
        }
    }

    private async Task<int> ApplyMissingFlagsAsync(
        IReadOnlyCollection<Guid> ids,
        bool missing,
        DateTime? since,
        CancellationToken cancellationToken)
    {
        var updated = 0;
        foreach (var chunk in ids.Distinct().Chunk(FlagChunkSize))
        {
            var set = chunk.ToArray();
            if (missing)
            {
                updated += await _db.MediaItems
                    .Where(row => set.Contains(row.Id) && !row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, since),
                        cancellationToken);
                await _db.TvShows.Where(row => set.Contains(row.Id) && !row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, since),
                        cancellationToken);
                await _db.Episodes.Where(row => set.Contains(row.Id) && !row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, since),
                        cancellationToken);
                await _db.Movies.Where(row => set.Contains(row.Id) && !row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, since),
                        cancellationToken);
                await _db.Music.Where(row => set.Contains(row.Id) && !row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, since),
                        cancellationToken);
                await _db.MusicVideos.Where(row => set.Contains(row.Id) && !row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, since),
                        cancellationToken);
                await _db.PastTenseNews.Where(row => set.Contains(row.Id) && !row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, since),
                        cancellationToken);
            }
            else
            {
                updated += await _db.MediaItems
                    .Where(row => set.Contains(row.Id) && row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, false).SetProperty(row => row.MissingSince, (DateTime?)null),
                        cancellationToken);
                await _db.TvShows.Where(row => set.Contains(row.Id) && row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, false).SetProperty(row => row.MissingSince, (DateTime?)null),
                        cancellationToken);
                await _db.Episodes.Where(row => set.Contains(row.Id) && row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, false).SetProperty(row => row.MissingSince, (DateTime?)null),
                        cancellationToken);
                await _db.Movies.Where(row => set.Contains(row.Id) && row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, false).SetProperty(row => row.MissingSince, (DateTime?)null),
                        cancellationToken);
                await _db.Music.Where(row => set.Contains(row.Id) && row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, false).SetProperty(row => row.MissingSince, (DateTime?)null),
                        cancellationToken);
                await _db.MusicVideos.Where(row => set.Contains(row.Id) && row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, false).SetProperty(row => row.MissingSince, (DateTime?)null),
                        cancellationToken);
                await _db.PastTenseNews.Where(row => set.Contains(row.Id) && row.IsMissing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.IsMissing, false).SetProperty(row => row.MissingSince, (DateTime?)null),
                        cancellationToken);
            }
        }

        return updated;
    }

    private async Task<int> MarkMissingNotSeenSinceAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var marked = await _db.MediaItems
            .Where(row => !row.IsMissing && row.SyncedAt < cutoffUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await _db.TvShows
            .Where(row => !row.IsMissing && row.SyncedAt < cutoffUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await _db.Episodes
            .Where(row => !row.IsMissing && row.SyncedAt < cutoffUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await _db.Movies
            .Where(row => !row.IsMissing && row.SyncedAt < cutoffUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await _db.Music
            .Where(row => !row.IsMissing && row.SyncedAt < cutoffUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await _db.MusicVideos
            .Where(row => !row.IsMissing && row.SyncedAt < cutoffUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        marked += await _db.PastTenseNews
            .Where(row => !row.IsMissing && row.SyncedAt < cutoffUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsMissing, true).SetProperty(row => row.MissingSince, now),
                cancellationToken);
        await _pool.PruneMissingAsync(cancellationToken);
        return marked;
    }

    private async Task<int> RemoveExpiredAsync(int gracePeriodDays, CancellationToken cancellationToken)
    {
        var expireBefore = DateTime.UtcNow.AddDays(-ClampGracePeriodDays(gracePeriodDays));
        var removed = await _db.MediaItems
            .Where(row => row.IsMissing && row.MissingSince != null && row.MissingSince <= expireBefore)
            .ExecuteDeleteAsync(cancellationToken);
        removed += await _db.TvShows
            .Where(row => row.IsMissing && row.MissingSince != null && row.MissingSince <= expireBefore)
            .ExecuteDeleteAsync(cancellationToken);
        removed += await _db.Episodes
            .Where(row => row.IsMissing && row.MissingSince != null && row.MissingSince <= expireBefore)
            .ExecuteDeleteAsync(cancellationToken);
        removed += await _db.Movies
            .Where(row => row.IsMissing && row.MissingSince != null && row.MissingSince <= expireBefore)
            .ExecuteDeleteAsync(cancellationToken);
        removed += await _db.Music
            .Where(row => row.IsMissing && row.MissingSince != null && row.MissingSince <= expireBefore)
            .ExecuteDeleteAsync(cancellationToken);
        removed += await _db.MusicVideos
            .Where(row => row.IsMissing && row.MissingSince != null && row.MissingSince <= expireBefore)
            .ExecuteDeleteAsync(cancellationToken);
        removed += await _db.PastTenseNews
            .Where(row => row.IsMissing && row.MissingSince != null && row.MissingSince <= expireBefore)
            .ExecuteDeleteAsync(cancellationToken);
        return removed;
    }

    private async Task<int> CountMissingAsync(CancellationToken cancellationToken)
    {
        var count = await _db.MediaItems.CountAsync(row => row.IsMissing, cancellationToken);
        return count;
    }

    private static DateTime? ResolveMarkCutoff(CatalogCleanupSettings settings)
    {
        if (settings.LastCatalogSyncStartedAt is DateTime started
            && settings.LastCatalogSyncCompletedAt is DateTime completed
            && completed >= started
            && completed > DateTime.UtcNow.AddDays(-2))
        {
            return started;
        }

        return null;
    }

    private async Task<DateTime?> ResolveFallbackCutoffAsync(CancellationToken cancellationToken)
    {
        var newest = await NewestSyncedAtAsync(cancellationToken);
        if (newest is DateTime synced && synced > DateTime.UtcNow.AddHours(-12))
        {
            return synced.AddHours(-2);
        }

        return null;
    }

    private async Task<DateTime?> NewestSyncedAtAsync(CancellationToken cancellationToken)
    {
        DateTime? newest = await _db.MediaItems.Select(row => (DateTime?)row.SyncedAt).MaxAsync(cancellationToken);
        newest = Later(newest, await _db.TvShows.Select(row => (DateTime?)row.SyncedAt).MaxAsync(cancellationToken));
        newest = Later(newest, await _db.Episodes.Select(row => (DateTime?)row.SyncedAt).MaxAsync(cancellationToken));
        newest = Later(newest, await _db.Movies.Select(row => (DateTime?)row.SyncedAt).MaxAsync(cancellationToken));
        newest = Later(newest, await _db.Music.Select(row => (DateTime?)row.SyncedAt).MaxAsync(cancellationToken));
        newest = Later(newest, await _db.MusicVideos.Select(row => (DateTime?)row.SyncedAt).MaxAsync(cancellationToken));
        newest = Later(newest, await _db.PastTenseNews.Select(row => (DateTime?)row.SyncedAt).MaxAsync(cancellationToken));
        return newest;
    }

    private static DateTime? Later(DateTime? left, DateTime? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left > right ? left : right;
    }

    private static CatalogCleanupSettings GetSettings()
        => FinTvRuntime.Current?.Configuration.CatalogCleanup ?? new CatalogCleanupSettings();
}

public sealed record CatalogCleanupStatus(
    int GracePeriodDays,
    bool IsRunning,
    int MarkedMissing,
    int Removed,
    string? LastError,
    DateTime? LastStartedAt,
    DateTime? LastCompletedAt,
    DateTime? LastCatalogSyncStartedAt,
    DateTime? LastCatalogSyncCompletedAt,
    int CurrentlyMissing,
    bool LocalScanIsRunning,
    int LocalScanTotalItems,
    int LocalScanProcessedItems,
    int LocalScanFound,
    int LocalScanMarkedMissing,
    int LocalScanRestored,
    int LocalScanSkipped,
    string? LocalScanLastError,
    DateTime? LocalScanLastStartedAt,
    DateTime? LocalScanLastCompletedAt);

public sealed record CatalogCleanupRunResult(int MarkedMissing, int Removed, int CurrentlyMissing, bool AlreadyRunning);

public sealed record CatalogLocalScanResult(int Found, int MarkedMissing, int Restored, int Skipped, bool AlreadyRunning);
