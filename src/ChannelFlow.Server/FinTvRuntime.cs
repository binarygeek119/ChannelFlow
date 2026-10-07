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
        EnsureNextResolverToken();
        ScheduleTimeZoneHelper.ApplyAsProcessTimeZone();
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
