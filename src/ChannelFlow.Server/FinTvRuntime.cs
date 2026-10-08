using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using FinTv.Services;
using Microsoft.EntityFrameworkCore;

namespace FinTv;

public sealed class FinTvRuntime
{
    public static FinTvRuntime Current { get; set; } = null!;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _configGate = new();
    private PluginConfiguration _configuration = new();

    public FinTvRuntime(IServiceScopeFactory scopeFactory, IWebHostEnvironment env, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        var configDir = AppEnvironment.FromConfiguration(config, "CONFIG")
            ?? Path.Combine(env.ContentRootPath, "config");
        DataFolder = configDir;
        LogosFolder = Path.Combine(configDir, "logos");
        EbsFolder = Path.Combine(configDir, "ebs");
        EbsCustomSlatesFolder = Path.Combine(EbsFolder, "custom");
        WeatherStarFolder = Path.Combine(configDir, "weatherstar");
        NewsFolder = Path.Combine(configDir, "news");
        LogsFolder = FileLogging.ResolveDirectory(env.ContentRootPath);
        var wwwroot = Path.Combine(env.ContentRootPath, "wwwroot");
        BundledLogosFolder = Path.Combine(wwwroot, "images", "logos");
        BundledMediaImagesFolder = Path.Combine(wwwroot, "images", "media");
        BundledAudioFolder = Path.Combine(wwwroot, "audio");
        BundledVideosFolder = Path.Combine(wwwroot, "videos");
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(LogosFolder);
        Directory.CreateDirectory(EbsCustomSlatesFolder);
        Directory.CreateDirectory(WeatherStarFolder);
        Directory.CreateDirectory(NewsFolder);
        Directory.CreateDirectory(LogsFolder);
        MusicFolder = Path.Combine(configDir, "music");
        Directory.CreateDirectory(MusicFolder);
        Current = this;
    }

    public string DataFolder { get; }

    public string LogosFolder { get; }

    public string EbsFolder { get; }

    public string EbsCustomSlatesFolder { get; }

    public string WeatherStarFolder { get; }

    public string NewsFolder { get; }

    public string MusicFolder { get; }

    public string LogsFolder { get; }

    public string BundledLogosFolder { get; }

    public string BundledMediaImagesFolder { get; }

    public string BundledAudioFolder { get; }

    public string BundledVideosFolder { get; }

    public IEnumerable<string> BundledAssetRoots()
    {
        yield return Path.Combine(LogosFolder, "binarygeek119");
        yield return BundledLogosFolder;
        yield return BundledMediaImagesFolder;
        yield return BundledAudioFolder;
        yield return BundledVideosFolder;
    }

    public IEnumerable<string> ExistingBundledAssetRoots()
    {
        foreach (var root in BundledAssetRoots())
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                yield return root;
            }
        }
    }

    public PluginConfiguration Configuration => _configuration;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var row = await db.AppSettings.AsNoTracking().OrderBy(r => r.Id).FirstOrDefaultAsync(cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.Json))
        {
            _configuration = new PluginConfiguration();
        }
        else
        {
            _configuration = FinTvJson.Deserialize<PluginConfiguration>(row.Json) ?? new PluginConfiguration();
        }

        EnsureApiKey();
        EnsureLocalPackMusicDefault();
        _configuration.Transcode ??= new TranscodeSettings();
        _configuration.Normalization ??= new NormalizationSettings();
        _configuration.NextTranscoding ??= new NextTranscodingSettings();
        _configuration.YouTube ??= new YouTubeSettings();
        EnsureNextDefaults();
        EnsureNextResolverToken();
        ScheduleTimeZoneHelper.ApplyAsProcessTimeZone();
    }

    /// <summary>
    /// next ships inside the ChannelFlow image, so a pristine install gets wired up out of
    /// the box instead of leaving <c>Enabled=false</c> and silently handing every player
    /// ChannelFlow's own MPEG-TS encode. Both base URLs are loopback because both processes
    /// run in this container.
    ///
    /// Applied only when both URLs are still unset — an untouched config. An explicit
    /// choice (a URL set, or next deliberately switched off on a box where it is installed)
    /// is left alone. Skipped entirely when no next binary is present, so a native
    /// <c>dotnet run</c> dev box keeps pointing at ChannelFlow's own encoder.
    /// </summary>
    private void EnsureNextDefaults()
    {
        var next = _configuration.NextTranscoding;
        if (!string.IsNullOrWhiteSpace(next.BaseUrl)
            || !string.IsNullOrWhiteSpace(next.ResolverBaseUrl))
        {
            return;
        }

        if (!FinTv.Next.NextCoordinatorService.IsBundled)
        {
            return;
        }

        next.Enabled = true;
        next.BaseUrl = "http://127.0.0.1:8409";
        next.ResolverBaseUrl = $"http://127.0.0.1:{HttpListenerPort()}";
        SaveConfiguration();
    }

    /// <summary>The port ChannelFlow itself listens on (loopback base URL for the resolver).</summary>
    private static int HttpListenerPort()
    {
        var raw = AppEnvironment.Get("PORT");
        return int.TryParse(raw, out var port) && port > 0 ? port : 8097;
    }

    private void EnsureLocalPackMusicDefault()
    {
        var dirty = false;
        if (_configuration.EbsBackgroundMusicSource == EbsBackgroundMusicSource.NamedLibrary
            && string.IsNullOrWhiteSpace(_configuration.EbsBackgroundMusicLibraryId))
        {
            _configuration.EbsBackgroundMusicSource = EbsBackgroundMusicSource.LocalPacks;
            dirty = true;
        }

        if (string.Equals(_configuration.WeatherMusicLibraryName, "Background Music", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(_configuration.WeatherMusicLibraryId))
        {
            _configuration.WeatherMusicLibraryName = "";
            dirty = true;
        }

        if (dirty)
        {
            SaveConfiguration();
        }
    }

    private void EnsureApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_configuration.ApiKey))
        {
            return;
        }

        _configuration.ApiKey = Auth.PluginApiKey.Generate();
        SaveConfiguration();
    }

    private void EnsureNextResolverToken()
    {
        if (!string.IsNullOrWhiteSpace(_configuration.NextTranscoding.ResolverToken))
        {
            return;
        }

        _configuration.NextTranscoding.ResolverToken = Auth.PluginApiKey.Generate();
        SaveConfiguration();
    }

    public void SaveConfiguration()
    {
        lock (_configGate)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
            var row = db.AppSettings.FirstOrDefault(r => r.Id == 1);
            if (row is null)
            {
                row = new AppSettingsRow { Id = 1 };
                db.AppSettings.Add(row);
            }

            row.Json = FinTvJson.Serialize(_configuration);
            db.Entry(row).Property(e => e.Json).IsModified = true;
            db.SaveChanges();
        }
    }
}
