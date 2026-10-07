using System.Text.Json;
using System.Text.RegularExpressions;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed record NewsBulletinRunResult(
    bool Created,
    bool Skipped,
    string? Path,
    int NewStoryCount,
    int EncodedStoryCount,
    string? SkipReason,
    DateTimeOffset RanAt);

public sealed class NewsBulletinService
{
    public const int IntervalHours = 6;
    private const int MaxKeptVideos = 2;
    private const int MaxTrackedStories = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly NewsHeadlineService _headlines;
    private readonly ILogger<NewsBulletinService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _busy;

    public NewsBulletinService(
        IServiceScopeFactory scopes,
        NewsHeadlineService headlines,
        ILogger<NewsBulletinService> logger)
    {
        _scopes = scopes;
        _headlines = headlines;
        _logger = logger;
    }

    public static DateTimeOffset NextSixHourMark(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var nextHour = ((local.Hour / IntervalHours) + 1) * IntervalHours;
        var next = nextHour >= 24
            ? local.Date.AddDays(1)
            : local.Date.AddHours(nextHour);
        return new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Unspecified), local.Offset);
    }

    public object DescribeStatus(NewsSettings? settings = null)
    {
        var ledger = LoadLedger();
        var enabled = settings?.BulletinVideosEnabled ?? true;
        var min = ClampMin(settings?.MinNewStories ?? 1);
        var running = IsRunning;
        return new
        {
            enabled,
            isRunning = running,
            intervalHours = IntervalHours,
            minNewStories = min,
            nextRunAt = NextSixHourMark(DateTimeOffset.Now),
            lastRunAt = ledger.LastRunAt,
            lastCreated = ledger.LastCreated,
            lastSkipReason = ledger.LastSkipReason,
            lastVideoPath = ledger.LastVideoPath,
            lastNewStoryCount = ledger.LastNewStoryCount,
            lastEncodedStoryCount = ledger.LastEncodedStoryCount,
            leftovers = DescribeLeftovers()
        };
    }

    public bool IsRunning => Volatile.Read(ref _busy) != 0;

    public bool TryQueue(bool scheduled = false, bool required = false)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(scheduled, required, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "News video run failed");
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
        return true;
    }

    public async Task<NewsBulletinRunResult> RunAsync(bool scheduled, CancellationToken cancellationToken)
        => await RunAsync(scheduled, required: false, cancellationToken);

    public async Task<NewsBulletinRunResult> RunAsync(bool scheduled, bool required, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _busy, 1);
        try
        {
            return await RunCoreAsync(scheduled, required, cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            _gate.Release();
            try
            {
                SweepFailedAndOld(keepCurrent: ResolvePlayableVideoPath(), keepNew: null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "News leftover cleanup after a run failed");
            }
        }
    }

    public bool ShouldRetryFailedEncode()
    {
        if (ResolvePlayableVideoPath() is not null)
        {
            return false;
        }

        var reason = LoadLedger().LastSkipReason;
        return !string.IsNullOrWhiteSpace(reason)
            && reason.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> EnsurePlayableAsync(CancellationToken cancellationToken)
    {
        var existing = ResolvePlayableVideoPath();
        if (existing is not null)
        {
            return existing;
        }

        TryQueue(scheduled: false, required: true);
        return null;
    }

    public string? ResolvePlayableVideoPath()
    {
        var ledger = LoadLedger();
        if (IsPlayableVideo(ledger.LastVideoPath))
        {
            return ledger.LastVideoPath;
        }

        return NewestPlayableVideo();
    }

    private async Task<NewsBulletinRunResult> RunCoreAsync(bool scheduled, bool required, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var renderer = scope.ServiceProvider.GetRequiredService<NewsChannelService>();
        var settings = await db.NewsSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken)
            ?? new NewsSettings();
        var ledger = LoadLedger();
        var ranAt = DateTimeOffset.Now;
        var min = ClampMin(settings.MinNewStories);
        var currentVideo = ResolvePlayableVideoPath();

        if (scheduled && !settings.BulletinVideosEnabled)
        {
            return SaveSkip(ledger, ranAt, "Bulletin videos are turned off.", 0);
        }

        var articles = await _headlines.GetAsync(force: true, cancellationToken);
        var used = new HashSet<string>(ledger.Keys, StringComparer.Ordinal);
        var newStories = articles.Where(article => !used.Contains(StoryKey(article))).ToList();
        var encoded = newStories.Take(Math.Clamp(settings.ArticleCount, 1, 30)).ToList();
        if (encoded.Count == 0 && required)
        {
            encoded = articles.Take(Math.Clamp(settings.ArticleCount, 1, 30)).ToList();
        }

        if (encoded.Count == 0)
        {
            return SaveSkip(ledger, ranAt, "No new stories.", 0);
        }

        if (!required && newStories.Count < min)
        {
            return SaveSkip(
                ledger,
                ranAt,
                $"Only {newStories.Count} new {(newStories.Count == 1 ? "story" : "stories")} (minimum {min}).",
                newStories.Count);
        }

        var newsRoot = FinTvRuntime.Current.NewsFolder;
        var stamp = ranAt.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var workDir = Path.Combine(newsRoot, "bulletins", "work-" + stamp);
        var outputMp4 = Path.Combine(newsRoot, "bulletins", $"news-{stamp}.mp4");
        var stagingMp4 = outputMp4 + ".partial";
        var header = NewsShowWriter.ResolveShowName(settings.HeaderText);

        var ok = false;
        string? encodeError = null;
        try
        {
            (ok, encodeError) = await renderer.RenderBulletinFileAsync(
                settings,
                encoded,
                header,
                workDir,
                stagingMp4,
                cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }

        if (!ok || !IsFinishedVideo(stagingMp4))
        {
            TryDeleteFile(stagingMp4);
            var reason = string.IsNullOrWhiteSpace(encodeError)
                ? "FFmpeg failed to create the news video."
                : "FFmpeg failed to create the news video. " + encodeError;
            return SaveSkip(ledger, ranAt, reason, encoded.Count);
        }

        try
        {
            File.Move(stagingMp4, outputMp4, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDeleteFile(stagingMp4);
            _logger.LogWarning(ex, "Could not publish news video {Path}", outputMp4);
            return SaveSkip(ledger, ranAt, "Could not publish the news video.", encoded.Count);
        }

        foreach (var story in encoded)
        {
            var key = StoryKey(story);
            if (!used.Contains(key))
            {
                ledger.Keys.Add(key);
            }
        }

        if (ledger.Keys.Count > MaxTrackedStories)
        {
            ledger.Keys = ledger.Keys.Skip(ledger.Keys.Count - MaxTrackedStories).ToList();
        }

        ledger.LastRunAt = ranAt;
        ledger.LastCreated = true;
        ledger.LastSkipReason = null;
        ledger.LastVideoPath = outputMp4;
        ledger.LastNewStoryCount = newStories.Count;
        ledger.LastEncodedStoryCount = encoded.Count;
        SaveLedger(ledger);
        SweepFailedAndOld(keepCurrent: currentVideo, keepNew: outputMp4);

        _logger.LogInformation(
            "News bulletin created {Path} with {Encoded} of {New} new stories",
            outputMp4,
            encoded.Count,
            newStories.Count);

        return new NewsBulletinRunResult(true, false, outputMp4, newStories.Count, encoded.Count, null, ranAt);
    }

    private NewsBulletinRunResult SaveSkip(NewsStoryLedger ledger, DateTimeOffset ranAt, string reason, int newCount)
    {
        ledger.LastRunAt = ranAt;
        ledger.LastCreated = false;
        ledger.LastSkipReason = reason;
        ledger.LastNewStoryCount = newCount;
        ledger.LastEncodedStoryCount = 0;
        SaveLedger(ledger);
        _logger.LogInformation("News bulletin skipped: {Reason}", reason);
        SweepFailedAndOld(keepCurrent: ledger.LastVideoPath, keepNew: null);
        return new NewsBulletinRunResult(false, true, ledger.LastVideoPath, newCount, 0, reason, ranAt);
    }

    public object SweepNow()
    {
        var current = ResolvePlayableVideoPath();
        var result = SweepFailedAndOld(keepCurrent: current, keepNew: null);
        _logger.LogInformation(
            "News cleanup removed {Work} work folders, {Partial} partial files, {Old} old videos, {Scratch} scratch files",
            result.WorkFolders,
            result.PartialFiles,
            result.OldVideos,
            result.ScratchFiles);
        return new
        {
            removedWorkFolders = result.WorkFolders,
            removedPartialFiles = result.PartialFiles,
            removedOldVideos = result.OldVideos,
            removedScratchFiles = result.ScratchFiles,
            keptVideoPath = current,
            bulletin = DescribeStatus()
        };
    }

    internal static int ClampMin(int value) => Math.Clamp(value <= 0 ? 1 : value, 1, 30);

    internal static string StoryKey(NewsArticle article)
    {
        var title = Regex.Replace((article.Title ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
        return title.Length > 240 ? title[..240] : title;
    }

    private static string LedgerPath()
        => Path.Combine(FinTvRuntime.Current.NewsFolder, "story-ledger.json");

    private NewsStoryLedger LoadLedger()
    {
        try
        {
            var path = LedgerPath();
            if (!File.Exists(path))
            {
                return new NewsStoryLedger();
            }

            var parsed = JsonSerializer.Deserialize<NewsStoryLedger>(File.ReadAllText(path), JsonOptions);
            return parsed ?? new NewsStoryLedger();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read news story ledger");
            return new NewsStoryLedger();
        }
    }

    private void SaveLedger(NewsStoryLedger ledger)
    {
        var path = LedgerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(ledger, JsonOptions));
    }

    private static bool IsPlayableVideo(string? path)
        => IsFinishedVideo(path) && !path!.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinishedVideo(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && File.Exists(path)
           && new FileInfo(path).Length > 1024;

    private static string? NewestPlayableVideo()
    {
        var folder = Path.Combine(FinTvRuntime.Current.NewsFolder, "bulletins");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        return Directory.GetFiles(folder, "news-*.mp4")
            .Where(path => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && IsPlayableVideo(path))
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => info.FullName)
            .FirstOrDefault();
    }

    private static object DescribeLeftovers()
    {
        var counts = CountLeftovers();
        return new
        {
            workFolders = counts.WorkFolders,
            partialFiles = counts.PartialFiles,
            extraVideos = counts.OldVideos,
            scratchFiles = counts.ScratchFiles
        };
    }

    private static NewsCleanupCounts CountLeftovers()
    {
        var newsRoot = FinTvRuntime.Current?.NewsFolder;
        if (string.IsNullOrWhiteSpace(newsRoot) || !Directory.Exists(newsRoot))
        {
            return new NewsCleanupCounts(0, 0, 0, 0);
        }

        var bulletinDir = Path.Combine(newsRoot, "bulletins");
        var work = CountWorkFolders(newsRoot, bulletinDir);
        var partial = CountPartialFiles(newsRoot, bulletinDir);
        var videos = ListBulletinVideos(bulletinDir).Count;
        var extraVideos = Math.Max(0, videos - MaxKeptVideos);
        var scratch = ScratchPaths(newsRoot).Count(path => File.Exists(path) || Directory.Exists(path));
        return new NewsCleanupCounts(work, partial, extraVideos, scratch);
    }

    private static int CountWorkFolders(string newsRoot, string bulletinDir)
    {
        var count = 0;
        if (Directory.Exists(newsRoot))
        {
            count += Directory.GetDirectories(newsRoot, "work-*").Length;
        }

        if (Directory.Exists(bulletinDir))
        {
            count += Directory.GetDirectories(bulletinDir, "work-*").Length;
        }

        return count;
    }

    private static int CountPartialFiles(string newsRoot, string bulletinDir)
    {
        var count = 0;
        foreach (var dir in new[] { newsRoot, bulletinDir })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            count += Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Count(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.Contains(".partial", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                });
        }

        return count;
    }

    private static List<FileInfo> ListBulletinVideos(string bulletinDir)
    {
        if (!Directory.Exists(bulletinDir))
        {
            return [];
        }

        return Directory.GetFiles(bulletinDir, "news-*.mp4")
            .Where(path => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .ToList();
    }

    private NewsCleanupCounts SweepFailedAndOld(string? keepCurrent, string? keepNew)
    {
        var newsRoot = FinTvRuntime.Current?.NewsFolder;
        if (string.IsNullOrWhiteSpace(newsRoot))
        {
            return new NewsCleanupCounts(0, 0, 0, 0);
        }

        Directory.CreateDirectory(newsRoot);
        var bulletinDir = Path.Combine(newsRoot, "bulletins");
        Directory.CreateDirectory(bulletinDir);

        // Skip files from a job still encoding so a cleanup click cannot kill the in-progress MP4.
        var cutoff = IsRunning ? DateTime.UtcNow.AddMinutes(-30) : DateTime.UtcNow.AddMinutes(1);
        var workFolders = 0;
        var partialFiles = 0;
        var oldVideos = 0;
        var scratchFiles = 0;

        foreach (var dir in Directory.GetDirectories(newsRoot, "work-*")
                     .Concat(Directory.GetDirectories(bulletinDir, "work-*")))
        {
            if (Directory.GetLastWriteTimeUtc(dir) > cutoff)
            {
                continue;
            }

            TryDeleteDirectory(dir);
            if (!Directory.Exists(dir))
            {
                workFolders++;
            }
        }

        foreach (var dir in new[] { newsRoot, bulletinDir })
        {
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (!name.Contains(".partial", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(file) > cutoff)
                {
                    continue;
                }

                TryDeleteFile(file);
                if (!File.Exists(file))
                {
                    partialFiles++;
                }
            }
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (IsPlayableVideo(keepCurrent))
        {
            keep.Add(keepCurrent!);
        }

        if (IsPlayableVideo(keepNew))
        {
            keep.Add(keepNew!);
        }

        var videos = ListBulletinVideos(bulletinDir)
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToList();
        var kept = 0;
        foreach (var file in videos)
        {
            if (file.Length > 1024 && keep.Contains(file.FullName))
            {
                kept++;
                continue;
            }

            if (file.Length > 1024 && kept < MaxKeptVideos)
            {
                kept++;
                continue;
            }

            TryDeleteFile(file.FullName);
            if (!File.Exists(file.FullName))
            {
                oldVideos++;
            }
        }

        foreach (var path in Directory.Exists(newsRoot)
                     ? Directory.GetFiles(newsRoot, "news-*.mp4")
                         .Where(path => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                     : [])
        {
            if (keep.Contains(path))
            {
                continue;
            }

            TryDeleteFile(path);
            if (!File.Exists(path))
            {
                oldVideos++;
            }
        }

        foreach (var path in ScratchPaths(newsRoot))
        {
            if (Directory.Exists(path))
            {
                TryDeleteDirectory(path);
                if (!Directory.Exists(path))
                {
                    scratchFiles++;
                }
            }
            else if (File.Exists(path))
            {
                TryDeleteFile(path);
                if (!File.Exists(path))
                {
                    scratchFiles++;
                }
            }
        }

        var ledger = LoadLedger();
        if (!string.IsNullOrWhiteSpace(ledger.LastVideoPath) && !File.Exists(ledger.LastVideoPath))
        {
            ledger.LastVideoPath = NewestPlayableVideo();
            SaveLedger(ledger);
        }

        return new NewsCleanupCounts(workFolders, partialFiles, oldVideos, scratchFiles);
    }

    private static IEnumerable<string> ScratchPaths(string newsRoot)
    {
        yield return Path.Combine(newsRoot, "speech.mp3");
        yield return Path.Combine(newsRoot, "news.ass");
        yield return Path.Combine(newsRoot, "images");
        yield return Path.Combine(newsRoot, "tts");
        yield return Path.Combine(newsRoot, "tts-ai");
    }

    private sealed record NewsCleanupCounts(int WorkFolders, int PartialFiles, int OldVideos, int ScratchFiles);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // staging files are replaced on the next successful run
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // work files are under config/news and will be overwritten next run
        }
    }

    private sealed class NewsStoryLedger
    {
        public List<string> Keys { get; set; } = [];

        public DateTimeOffset? LastRunAt { get; set; }

        public bool LastCreated { get; set; }

        public string? LastSkipReason { get; set; }

        public string? LastVideoPath { get; set; }

        public int LastNewStoryCount { get; set; }

        public int LastEncodedStoryCount { get; set; }
    }
}
