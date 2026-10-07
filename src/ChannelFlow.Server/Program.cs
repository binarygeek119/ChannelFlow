using System.Net;
using FinTv;
using FinTv.Auth;
using FinTv.Data;
using FinTv.Domain;
using FinTv.News;
using FinTv.Services;
using FinTv.Services.MediaServers;
using FinTv.Streaming;
using FinTv.Weather;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Serilog;

LoadDotEnv();
Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");

var builder = WebApplication.CreateBuilder(args);
FileLogging.Configure(builder);

builder.Services.AddSingleton<PostgresConnectionStore>();
builder.AddReverseProxySupport();
builder.Services.AddDbContext<FinTvDbContext>((sp, options) =>
{
    var connectionString = sp.GetRequiredService<PostgresConnectionStore>().GetConnectionString()
        ?? "Host=127.0.0.1;Port=1;Database=channelflow;Username=channelflow;Password=unset;Timeout=1;Command Timeout=1";
    options.UseNpgsql(connectionString);
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.AllowTrailingCommas = true;
    });

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "channelflow.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.Events.OnRedirectToLogin = context =>
        {
            if (IsApiOrStream(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (IsApiOrStream(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("admin", policy => policy.RequireAuthenticatedUser());
});

builder.Services.AddHttpClient(nameof(LlmClientService))
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All
    });
builder.Services.AddTransient<CommercialBrainzRateLimitHandler>();
builder.Services.AddHttpClient(nameof(CommercialBrainzClient))
    .AddHttpMessageHandler<CommercialBrainzRateLimitHandler>()
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/0.0.3 (CommercialBrainz)");
    });
builder.Services.AddHttpClient("Weather", client =>
{
    client.Timeout = TimeSpan.FromSeconds(25);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/1.0 (https://github.com/binarygeek119/ChannelFlow)");
});
builder.Services.AddHttpClient("News", client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/0.0.3 (news)");
});
builder.Services.AddHttpClient("JellyfinPlugin", client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/1.0 (guide-refresh)");
});
builder.Services.AddHttpClient("MediaServer", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/1.0 (media-server)");
});
builder.Services.AddHttpClient(nameof(SponsorBlockClient), client =>
{
    client.Timeout = TimeSpan.FromSeconds(8);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/1.0 (SponsorBlock)");
});
builder.Services.AddHttpClient(QuickPinService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/1.0 (quick-pin)");
});
builder.Services.AddHttpClient(nameof(MusicPackService))
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/1.0 (music-packs)");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All
    });
builder.Services.AddHttpClient();

builder.Services.AddSingleton<FinTvRuntime>();
builder.Services.AddSingleton<FfmpegLocator>();
builder.Services.AddSingleton<IFfmpegLocator>(sp => sp.GetRequiredService<FfmpegLocator>());
builder.Services.AddSingleton<PublicBaseUrl>();
builder.Services.AddSingleton<IPublicBaseUrl>(sp => sp.GetRequiredService<PublicBaseUrl>());
builder.Services.AddSingleton<GpuCapabilityService>();
builder.Services.AddSingleton<FfmpegEncodingService>();
builder.Services.AddSingleton<StreamNormalizationService>();
builder.Services.AddSingleton<FfmpegCommandBuilder>();
builder.Services.AddSingleton<YtDlpLocator>();
builder.Services.AddSingleton<YouTubeCookieStore>();
builder.Services.AddSingleton<MusicPackService>();
builder.Services.AddSingleton<StreamService>();
builder.Services.AddSingleton<WeatherGeocoder>();
builder.Services.AddSingleton<WeatherDataClient>();
builder.Services.AddSingleton<WeatherStarAssets>();
builder.Services.AddSingleton<WeatherStarCompositor>();
builder.Services.AddSingleton<WeatherAlertOverlayService>();
builder.Services.AddSingleton<AiChannelGenerateJobService>();
builder.Services.AddSingleton<AiLineupAutoApplyTask>();
builder.Services.AddSingleton<CatalogCleanupTask>();
builder.Services.AddSingleton<CatalogLibraryScanService>();
builder.Services.AddSingleton<CatalogLibraryScanTask>();
builder.Services.AddSingleton<CatalogFfprobeScanService>();
builder.Services.AddSingleton<CatalogFfprobeScanTask>();
builder.Services.AddSingleton<CatalogTrueAspectScanService>();
builder.Services.AddSingleton<CatalogTrueAspectScanTask>();
builder.Services.AddSingleton<CatalogCommercialBreakScanService>();
builder.Services.AddSingleton<CatalogCommercialBreakScanTask>();
builder.Services.AddSingleton<PlayoutBuilderService>();
builder.Services.AddSingleton<GuideUpdateTracker>();
builder.Services.AddSingleton<BlackframeChapterTask>();

builder.Services.AddScoped<PathRemapService>();
builder.Services.AddScoped<CatalogLibraryManager>();
builder.Services.AddScoped<CatalogTypedStore>();
builder.Services.AddScoped<CatalogCleanupService>();
builder.Services.AddScoped<ILibraryManager>(sp => sp.GetRequiredService<CatalogLibraryManager>());
builder.Services.AddScoped<IChapterManager>(sp => sp.GetRequiredService<CatalogLibraryManager>());
builder.Services.AddScoped<ChannelService>();
builder.Services.AddScoped<ChannelPresetService>();
builder.Services.AddScoped<LineupService>();
builder.Services.AddScoped<LineupSlotKindService>();
builder.Services.AddScoped<SpecialPresentationService>();
builder.Services.AddScoped<FinTvListService>();
builder.Services.AddScoped<SmartSelectionService>();
builder.Services.AddScoped<LineupGeneratorService>();
builder.Services.AddScoped<CommercialService>();
builder.Services.AddSingleton<CommercialBrainzClient>();
builder.Services.AddScoped<CommercialBrainzFilterService>();
builder.Services.AddScoped<CommercialBrainzSyncService>();
builder.Services.AddScoped<SponsorBlockClient>();
builder.Services.AddScoped<YouTubeCommercialStreamService>();
builder.Services.AddScoped<EpgService>();
builder.Services.AddScoped<GuideMetadataService>();
builder.Services.AddScoped<WeatherGuideMetadataService>();
builder.Services.AddScoped<LogoSetService>();
builder.Services.AddScoped<LogoBumperService>();
builder.Services.AddScoped<HolidayChannelService>();
builder.Services.AddScoped<CatalogIngestService>();
builder.Services.AddScoped<CatalogChapterProbeService>();
builder.Services.AddScoped<CatalogTrueAspectProbeService>();
builder.Services.AddScoped<CatalogCommercialBreakProbeService>();
builder.Services.AddScoped<JellyfinCatalogService>();
builder.Services.AddScoped<OriginalBroadcastSimulator>();
builder.Services.AddScoped<AiCatalogManifestBuilder>();
        builder.Services.AddScoped<ChannelCatalogPoolService>();
        builder.Services.AddScoped<ChannelPrimetimeService>();
        builder.Services.AddScoped<MusicVideoChannelListService>();
builder.Services.AddScoped<LlmClientService>();
builder.Services.AddScoped<AiLineupGeneratorService>();
builder.Services.AddScoped<AiChannelAutoApplyService>();
builder.Services.AddScoped<EbsService>();
builder.Services.AddSingleton<NewsHeadlineService>();
builder.Services.AddSingleton<NewsTtsService>();
builder.Services.AddScoped<NewsShowWriter>();
builder.Services.AddSingleton<NewsBulletinService>();
builder.Services.AddSingleton<NewsBulletinTask>();
builder.Services.AddScoped<WeatherStarChannelService>();
builder.Services.AddScoped<NewsChannelService>();
builder.Services.AddSingleton<CatalogSyncProgress>();
builder.Services.AddSingleton<QuickPinService>();
builder.Services.AddSingleton<ClientLogStore>();
builder.Services.AddSingleton<PairedTvClientStore>();
builder.Services.AddSingleton<JellyfinMediaServerProvider>();
builder.Services.AddSingleton<SidecarMediaServerProvider>();
builder.Services.AddSingleton<IMediaServerProvider>(sp => sp.GetRequiredService<JellyfinMediaServerProvider>());
builder.Services.AddSingleton<IMediaServerProvider>(sp => sp.GetRequiredService<SidecarMediaServerProvider>());
builder.Services.AddSingleton<IMediaServerProvider>(_ => new PlaceholderMediaServerProvider(MediaServerKind.Emby));
builder.Services.AddSingleton<IMediaServerProvider>(_ => new PlaceholderMediaServerProvider(MediaServerKind.Plex));
builder.Services.AddSingleton<IMediaServerProvider>(_ => new PlaceholderMediaServerProvider(MediaServerKind.Other));
builder.Services.AddScoped<MediaServerService>();

builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseInitializer>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlayoutBuilderService>());
builder.Services.AddHostedService<ScheduledTaskHost>();
builder.Services.AddHostedService<NewsRefreshHostedService>();
builder.Services.AddHostedService<NewsBulletinHostedService>();
builder.Services.AddHostedService<WeatherGuideRefreshHostedService>();
builder.Services.AddHostedService<AiPlayoutHorizonHostedService>();
builder.Services.AddHostedService<CommercialBrainzRefreshHostedService>();
builder.Services.AddHostedService<MusicPackStartupHostedService>();
builder.Services.AddHostedService<LogRetentionHostedService>();
builder.Services.AddHostedService<MediaServerHealthHostedService>();
builder.Services.AddHostedService<CatalogLibraryScanHostedService>();
builder.Services.AddHostedService<CatalogFfprobeScanHostedService>();

var app = builder.Build();

app.UseReverseProxy();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Text("ok"))
    .AllowAnonymous()
    .ShortCircuit();
app.MapControllers();
app.MapSpaFallback();

var port = Environment.GetEnvironmentVariable("PORT") ?? "8097";
app.Urls.Add($"http://0.0.0.0:{port}");

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ChannelFlow-Server terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

static bool IsApiOrStream(PathString path)
    => path.StartsWithSegments("/api") || path.StartsWithSegments("/iptv");

static void LoadDotEnv()
{
    DirectoryInfo? cursor = new(Directory.GetCurrentDirectory());
    for (var i = 0; i < 8 && cursor is not null; i++, cursor = cursor.Parent)
    {
        var file = Path.Combine(cursor.FullName, ".env");
        if (!File.Exists(file))
        {
            continue;
        }

        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"').Trim('\'');
            if (string.IsNullOrWhiteSpace(key) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, value);
        }

        return;
    }
}
