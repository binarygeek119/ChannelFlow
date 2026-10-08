using FinTv.Data;
using FinTv.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.Data;

public class DatabaseInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PostgresConnectionStore _postgres;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly SemaphoreSlim _init = new(1, 1);
    private volatile bool _ready;

    public DatabaseInitializer(
        IServiceScopeFactory scopeFactory,
        PostgresConnectionStore postgres,
        ILogger<DatabaseInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _postgres = postgres;
        _logger = logger;
    }

    public bool IsReady => _ready;

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        while (!_ready)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await TryInitializeAsync(cancellationToken))
            {
                _logger.LogInformation(
                    "PostgreSQL is not configured yet. Open the ChannelFlow web UI to finish first-run database setup.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "PostgreSQL is not reachable yet. Open the ChannelFlow web UI to finish database setup.");
        }
    }

    public async Task<bool> TryInitializeAsync(CancellationToken cancellationToken)
    {
        if (!_postgres.IsConfigured)
        {
            return false;
        }

        if (_ready)
        {
            return true;
        }

        await _init.WaitAsync(cancellationToken);
        try
        {
            if (_ready)
            {
                return true;
            }

            try
            {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
            await db.Database.EnsureCreatedAsync(cancellationToken);
        await CatalogSchema.EnsureEpisodesTableAsync(db, cancellationToken);
        await EnsureNewsColumnsAsync(db, cancellationToken);
        await RenameFlowWireChannelAsync(db, cancellationToken);
        await MigrateRetiredNewsChannelAsync(db, cancellationToken);
        await EnsureChannelColumnsAsync(db, cancellationToken);
        await EnsureLineupSlotColumnsAsync(db, cancellationToken);
        await EnsureMediaItemColumnsAsync(db, cancellationToken);
        await EnsureCatalogTablesAsync(db, cancellationToken);
        await EnsureAiPoolTablesAsync(db, cancellationToken);
        await EnsurePrimetimeTablesAsync(db, cancellationToken);
        await EnsurePlayoutItemExternalUrlAsync(db, cancellationToken);
        await UpgradeTvShowsToEpisodesAsync(db, cancellationToken);
        await EnsureCatalogMissingColumnsAsync(db, cancellationToken);
        await EnsureMediaServerSchemaAsync(db, cancellationToken);
        var typedCatalog = scope.ServiceProvider.GetRequiredService<CatalogTypedStore>();
        await typedCatalog.BackfillFromMediaItemsAsync(cancellationToken);
        await typedCatalog.NormalizeAspectRatiosAsync(cancellationToken);

        if (!await db.AppSettings.AnyAsync(cancellationToken))
        {
            db.AppSettings.Add(new Domain.AppSettingsRow
            {
                Id = 1,
                Json = Domain.FinTvJson.Serialize(new Configuration.PluginConfiguration())
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        if (!await db.CommercialPresets.AnyAsync(cancellationToken))
        {
            db.CommercialPresets.Add(new Domain.CommercialPreset
            {
                Name = "Default",
                BreakMode = Domain.CommercialBreakMode.ChaptersThenTimer,
                TimerIntervalMinutes = 12,
                PreRollCount = 2,
                MidRollCount = 2,
                PostRollCount = 2
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        var runtime = scope.ServiceProvider.GetRequiredService<FinTvRuntime>();
        await runtime.LoadAsync(cancellationToken);
        FinTvRuntime.Current = runtime;
        if (CatalogCleanupService.ResetInterruptedState())
        {
            _logger.LogWarning(
                "Catalog cleanup was still flagged running from a previous process; cleared the stale state so it can run again");
        }
        var gpu = scope.ServiceProvider.GetRequiredService<FinTv.Streaming.GpuCapabilityService>();
        await gpu.GetAsync(cancellationToken);
        var encoding = scope.ServiceProvider.GetRequiredService<FinTv.Streaming.FfmpegEncodingService>();
        encoding.ApplyFromSaved(runtime.Configuration.Transcode);
        var normalization = scope.ServiceProvider.GetRequiredService<FinTv.Streaming.StreamNormalizationService>();
        var clampedNorm = gpu.ClampNormalization(
            runtime.Configuration.Normalization ?? Configuration.NormalizationSettings.CreateDefault(),
            encoding.Describe().HardwareAcceleration);
        normalization.ApplyFromSaved(clampedNorm);
        await EnsureSavedWeatherLocationsAsync(db, runtime, cancellationToken);

        try
        {
            await scope.ServiceProvider.GetRequiredService<LogoSetService>()
                .EnsureBinarygeek119SetAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FlowWire logo ensure skipped");
        }

        _logger.LogInformation("ChannelFlow-Server database initialized");
            _ready = true;
            return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
            {
                _logger.LogWarning(ex, "PostgreSQL initialize failed");
                throw new InvalidOperationException("Could not initialize the database. " + ex.Message, ex);
            }
        }
        finally
        {
            _init.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task EnsureSavedWeatherLocationsAsync(
        FinTvDbContext db,
        FinTvRuntime runtime,
        CancellationToken cancellationToken)
    {
        var defaultLocation = WeatherStarChannelService.IsUnsetOrLegacyLocation(runtime.Configuration.WeatherDefaultLocationQuery)
            ? null
            : runtime.Configuration.WeatherDefaultLocationQuery!.Trim();
        if (string.IsNullOrWhiteSpace(defaultLocation))
        {
            return;
        }

        var channels = await db.Channels
            .Where(c => c.ContentType == Domain.ChannelContentType.Weather)
            .ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            if (!WeatherStarChannelService.IsUnsetOrLegacyLocation(channel.WeatherLocationQuery))
            {
                continue;
            }

            channel.WeatherLocationQuery = defaultLocation;
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task EnsureNewsColumnsAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "ShowHeader" boolean NOT NULL DEFAULT TRUE""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "ReadHeadlinesOnly" boolean NOT NULL DEFAULT FALSE""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "IntroText" text NULL""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "OutroText" text NULL""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "RefreshMinutes" integer NOT NULL DEFAULT 10""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "MinNewStories" integer NOT NULL DEFAULT 1""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "BulletinVideosEnabled" boolean NOT NULL DEFAULT TRUE""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "TtsEngine" text NOT NULL DEFAULT 'google'""",
            """ALTER TABLE "NewsSettings" ADD COLUMN IF NOT EXISTS "AiRewrite" boolean NOT NULL DEFAULT FALSE"""
        };

        foreach (var sql in statements)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "News schema ensure skipped for {Sql}", sql);
            }
        }
    }

    private async Task RenameFlowWireChannelAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            """UPDATE "Channels" SET "Name" = 'FlowWire News' WHERE "Name" IN ('FlowWire', 'ChannelFlow News', 'ChannelFlow', 'FinTV News', 'FinTV', 'FinTV News') OR "Name" ILIKE '%fintv%'""",
            """UPDATE "NewsSettings" SET "HeaderText" = 'FlowWire News' WHERE "HeaderText" IN ('ChannelFlow News', 'ChannelFlow', 'FlowWire', 'FinTV News', 'FinTV', 'FinTV News', '') OR "HeaderText" IS NULL OR "HeaderText" ILIKE '%fintv%' OR "HeaderText" ILIKE '%channelflow%'""",
            """UPDATE "NewsSettings" SET "IntroText" = REGEXP_REPLACE("IntroText", 'fin[[:space:]]*tv([[:space:]]+news)?', 'FlowWire News', 'gi') WHERE "IntroText" ~* 'fintv|fin[[:space:]]*tv|channelflow'""",
            """UPDATE "NewsSettings" SET "OutroText" = REGEXP_REPLACE("OutroText", 'fin[[:space:]]*tv([[:space:]]+news)?', 'FlowWire News', 'gi') WHERE "OutroText" ~* 'fintv|fin[[:space:]]*tv|channelflow'""",
            """UPDATE "NewsSettings" SET "IntroText" = REPLACE("IntroText", 'ChannelFlow News', 'FlowWire News') WHERE "IntroText" LIKE '%ChannelFlow News%'""",
            """UPDATE "NewsSettings" SET "OutroText" = REPLACE("OutroText", 'ChannelFlow News', 'FlowWire News') WHERE "OutroText" LIKE '%ChannelFlow News%'"""
        };

        foreach (var sql in statements)
        {
            try
            {
                var updated = await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
                if (updated > 0)
                {
                    _logger.LogInformation("Renamed news channel to FlowWire News ({Updated} row(s))", updated);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FlowWire rename skipped for {Sql}", sql);
            }
        }
    }

    private async Task MigrateRetiredNewsChannelAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        List<Domain.Channel> channels;
        try
        {
            channels = await db.Channels.ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Retired news channel lookup skipped");
            return;
        }

        var changed = 0;
        foreach (var channel in channels)
        {
            var tag = Domain.ChannelAiRules.ExtractLibraryTag(channel.FilterJson);
            var retiredName = string.Equals(channel.Name, "BinaryGeek119 News", StringComparison.OrdinalIgnoreCase);
            var retiredTag = Domain.FilterDefinition.PresetIdsEqual(tag, "channelflow-news");
            if (!retiredName && !retiredTag)
            {
                continue;
            }

            channel.Name = "FlowWire News";
            channel.ContentType = Domain.ChannelContentType.News;
            var filter = Domain.FilterDefinition.Parse(channel.FilterJson) ?? new Domain.FilterDefinition();
            filter.PresetId = "channelflow-live-news";
            channel.FilterJson = Domain.FinTvJson.Serialize(filter);
            changed++;
            _logger.LogInformation(
                "Kept news channel {ChannelId} and migrated it to FlowWire News so existing IPTV URLs keep working",
                channel.Id);
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task EnsureChannelColumnsAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            """ALTER TABLE "Channels" ADD COLUMN IF NOT EXISTS "CommercialSearchPlaylistIdsJson" text NULL""",
            """ALTER TABLE "Channels" ADD COLUMN IF NOT EXISTS "RtspUrl" text NULL""",
        };
        foreach (var sql in statements)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Channel schema ensure skipped");
            }
        }
    }

    private async Task EnsureLineupSlotColumnsAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "LineupSlots" ADD COLUMN IF NOT EXISTS "IsRerunSlot" boolean NOT NULL DEFAULT FALSE""",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LineupSlot schema ensure skipped");
        }
    }

    private async Task EnsureMediaItemColumnsAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "CommunityRating" real NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "CriticRating" real NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "Runtime" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "Album" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "MediaType" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "SeasonId" uuid NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "SeasonName" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "PeopleJson" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "ProviderIdsJson" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "ArtistsJson" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "AlbumArtistsJson" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "Width" integer NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "Height" integer NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "AspectRatio" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "FfprobeChaptersAt" timestamp with time zone NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "TrueAspectRatio" text NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "TrueAspectProbedAt" timestamp with time zone NULL""",
            """ALTER TABLE "MediaItems" ADD COLUMN IF NOT EXISTS "CommercialBreaksProbedAt" timestamp with time zone NULL""",
            """CREATE INDEX IF NOT EXISTS "IX_MediaItems_AspectRatio" ON "MediaItems" ("AspectRatio")""",
            """CREATE INDEX IF NOT EXISTS "IX_MediaItems_FfprobeChaptersAt" ON "MediaItems" ("FfprobeChaptersAt")""",
            """CREATE INDEX IF NOT EXISTS "IX_MediaItems_TrueAspectRatio" ON "MediaItems" ("TrueAspectRatio")""",
            """CREATE INDEX IF NOT EXISTS "IX_MediaItems_TrueAspectProbedAt" ON "MediaItems" ("TrueAspectProbedAt")""",
            """CREATE INDEX IF NOT EXISTS "IX_MediaItems_CommercialBreaksProbedAt" ON "MediaItems" ("CommercialBreaksProbedAt")""",
            """CREATE INDEX IF NOT EXISTS "IX_MediaItems_CatalogScope" ON "MediaItems" ("SourceConnectionId", "Kind", "IsMissing", "SeriesId")"""
        };

        foreach (var sql in statements)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MediaItem schema ensure skipped for {Sql}", sql);
            }
        }
    }

    private async Task EnsureCatalogTablesAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var shared = """
            "Id" uuid NOT NULL,
            "Name" text NOT NULL,
            "SortName" text NULL,
            "Plot" text NULL,
            "OfficialRating" text NULL,
            "CommunityRating" double precision NULL,
            "CriticRating" double precision NULL,
            "ProductionYear" integer NULL,
            "PremiereDate" timestamp with time zone NULL,
            "RuntimeTicks" bigint NULL,
            "Format" text NULL,
            "VideoCodec" text NULL,
            "AudioCodec" text NULL,
            "Width" integer NULL,
            "Height" integer NULL,
            "AspectRatio" text NULL,
            "TrueAspectRatio" text NULL,
            "Path" text NULL,
            "JellyfinItemId" uuid NOT NULL,
            "ImdbId" text NULL,
            "TmdbId" text NULL,
            "TvdbId" text NULL,
            "MusicBrainzId" text NULL,
            "ProviderIdsJson" text NOT NULL DEFAULT '{}',
            "LibraryId" uuid NULL,
            "LibraryName" text NULL,
            "PrimaryImagePath" text NULL,
            "GenresJson" text NOT NULL DEFAULT '[]',
            "StarsJson" text NOT NULL DEFAULT '[]',
            "StudiosJson" text NOT NULL DEFAULT '[]',
            "TagsJson" text NOT NULL DEFAULT '[]',
            "ChaptersJson" text NOT NULL DEFAULT '[]',
            "SyncedAt" timestamp with time zone NOT NULL,
            "IsMissing" boolean NOT NULL DEFAULT FALSE,
            "MissingSince" timestamp with time zone NULL,
            """;

        var statements = new[]
        {
            $"""CREATE TABLE IF NOT EXISTS "TvShows" ({shared} CONSTRAINT "PK_TvShows" PRIMARY KEY ("Id"))""",
            $"""CREATE TABLE IF NOT EXISTS "Movies" ({shared} CONSTRAINT "PK_Movies" PRIMARY KEY ("Id"))""",
            $"""CREATE TABLE IF NOT EXISTS "Music" ({shared} "Album" text NULL, "AlbumArtist" text NULL, "ArtistsJson" text NOT NULL DEFAULT '[]', "TrackNumber" integer NULL, "DiscNumber" integer NULL, CONSTRAINT "PK_Music" PRIMARY KEY ("Id"))""",
            $"""CREATE TABLE IF NOT EXISTS "MusicVideos" ({shared} "Album" text NULL, "ArtistsJson" text NOT NULL DEFAULT '[]', CONSTRAINT "PK_MusicVideos" PRIMARY KEY ("Id"))""",
            $"""CREATE TABLE IF NOT EXISTS "PastTenseNews" ({shared} "SeriesId" uuid NULL, "SeriesName" text NULL, "SeasonNumber" integer NULL, "EpisodeNumber" integer NULL, CONSTRAINT "PK_PastTenseNews" PRIMARY KEY ("Id"))""",
            """CREATE INDEX IF NOT EXISTS "IX_TvShows_Name" ON "TvShows" ("Name")""",
            """CREATE INDEX IF NOT EXISTS "IX_TvShows_LibraryId" ON "TvShows" ("LibraryId")""",
            """CREATE INDEX IF NOT EXISTS "IX_TvShows_JellyfinItemId" ON "TvShows" ("JellyfinItemId")""",
            """CREATE INDEX IF NOT EXISTS "IX_Movies_Name" ON "Movies" ("Name")""",
            """CREATE INDEX IF NOT EXISTS "IX_Movies_LibraryId" ON "Movies" ("LibraryId")""",
            """CREATE INDEX IF NOT EXISTS "IX_Movies_JellyfinItemId" ON "Movies" ("JellyfinItemId")""",
            """CREATE INDEX IF NOT EXISTS "IX_Music_Name" ON "Music" ("Name")""",
            """CREATE INDEX IF NOT EXISTS "IX_Music_LibraryId" ON "Music" ("LibraryId")""",
            """CREATE INDEX IF NOT EXISTS "IX_Music_JellyfinItemId" ON "Music" ("JellyfinItemId")""",
            """CREATE INDEX IF NOT EXISTS "IX_MusicVideos_Name" ON "MusicVideos" ("Name")""",
            """CREATE INDEX IF NOT EXISTS "IX_MusicVideos_LibraryId" ON "MusicVideos" ("LibraryId")""",
            """CREATE INDEX IF NOT EXISTS "IX_MusicVideos_JellyfinItemId" ON "MusicVideos" ("JellyfinItemId")""",
            """CREATE INDEX IF NOT EXISTS "IX_PastTenseNews_Name" ON "PastTenseNews" ("Name")""",
            """CREATE INDEX IF NOT EXISTS "IX_PastTenseNews_LibraryId" ON "PastTenseNews" ("LibraryId")""",
            """CREATE INDEX IF NOT EXISTS "IX_PastTenseNews_JellyfinItemId" ON "PastTenseNews" ("JellyfinItemId")"""
        };

        foreach (var sql in statements)
        {
            try
            {
                await CatalogSchema.ExecuteAsync(db, sql, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catalog table ensure failed for {Sql}", sql);
            }
        }
    }

    private async Task EnsureAiPoolTablesAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "ChannelCatalogPool" (
                "Id" uuid NOT NULL,
                "ChannelId" uuid NOT NULL,
                "JellyfinItemId" uuid NOT NULL,
                "Kind" text NOT NULL,
                "PickedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_ChannelCatalogPool" PRIMARY KEY ("Id")
            )
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChannelCatalogPool_ChannelId_JellyfinItemId" ON "ChannelCatalogPool" ("ChannelId", "JellyfinItemId")""",
            """CREATE INDEX IF NOT EXISTS "IX_ChannelCatalogPool_JellyfinItemId" ON "ChannelCatalogPool" ("JellyfinItemId")""",
            """
            CREATE TABLE IF NOT EXISTS "MusicVideoChannelArtists" (
                "Id" uuid NOT NULL,
                "ChannelId" uuid NOT NULL,
                "ArtistName" text NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_MusicVideoChannelArtists" PRIMARY KEY ("Id")
            )
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_MusicVideoChannelArtists_ChannelId_ArtistName" ON "MusicVideoChannelArtists" ("ChannelId", "ArtistName")""",
            """
            CREATE TABLE IF NOT EXISTS "MusicVideoYoutubeSources" (
                "Id" uuid NOT NULL,
                "ChannelId" uuid NOT NULL,
                "SourceUrl" text NOT NULL,
                "YoutubeVideoId" text NULL,
                "Title" text NULL,
                "Artist" text NULL,
                "DurationSeconds" integer NULL,
                "IsPlaylist" boolean NOT NULL DEFAULT FALSE,
                "ParentSourceId" uuid NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_MusicVideoYoutubeSources" PRIMARY KEY ("Id")
            )
            """,
            """CREATE INDEX IF NOT EXISTS "IX_MusicVideoYoutubeSources_ChannelId" ON "MusicVideoYoutubeSources" ("ChannelId")"""
        };

        foreach (var sql in statements)
        {
            try
            {
                await CatalogSchema.ExecuteAsync(db, sql, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI pool table ensure failed");
            }
        }
    }

    private async Task EnsurePrimetimeTablesAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "ChannelPrimetimeSlots" (
                "Id" uuid NOT NULL,
                "ChannelId" uuid NOT NULL,
                "SlotIndex" integer NOT NULL,
                CONSTRAINT "PK_ChannelPrimetimeSlots" PRIMARY KEY ("Id")
            )
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChannelPrimetimeSlots_ChannelId_SlotIndex" ON "ChannelPrimetimeSlots" ("ChannelId", "SlotIndex")""",
            """
            CREATE TABLE IF NOT EXISTS "ChannelPrimetimeCandidates" (
                "Id" uuid NOT NULL,
                "SlotId" uuid NOT NULL,
                "SeriesId" uuid NOT NULL,
                "Title" text NOT NULL,
                "SortOrder" integer NOT NULL,
                CONSTRAINT "PK_ChannelPrimetimeCandidates" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ChannelPrimetimeCandidates_ChannelPrimetimeSlots_SlotId"
                    FOREIGN KEY ("SlotId") REFERENCES "ChannelPrimetimeSlots" ("Id") ON DELETE CASCADE
            )
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ChannelPrimetimeCandidates_SlotId" ON "ChannelPrimetimeCandidates" ("SlotId")""",
            """CREATE INDEX IF NOT EXISTS "IX_ChannelPrimetimeCandidates_SeriesId" ON "ChannelPrimetimeCandidates" ("SeriesId")"""
        };

        foreach (var sql in statements)
        {
            try
            {
                await CatalogSchema.ExecuteAsync(db, sql, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Primetime table ensure failed");
            }
        }
    }

    private async Task EnsurePlayoutItemExternalUrlAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "PlayoutItems" ADD COLUMN IF NOT EXISTS "ExternalUrl" text NULL""",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PlayoutItem ExternalUrl ensure skipped");
        }
    }

    private async Task EnsureCatalogMissingColumnsAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var tables = new[] { "MediaItems", "TvShows", "Episodes", "Movies", "Music", "MusicVideos", "PastTenseNews" };
        foreach (var table in tables)
        {
            var statements = new[]
            {
                $"""ALTER TABLE "{table}" ADD COLUMN IF NOT EXISTS "IsMissing" boolean NOT NULL DEFAULT FALSE""",
                $"""ALTER TABLE "{table}" ADD COLUMN IF NOT EXISTS "MissingSince" timestamp with time zone NULL""",
                $"""ALTER TABLE "{table}" ADD COLUMN IF NOT EXISTS "Width" integer NULL""",
                $"""ALTER TABLE "{table}" ADD COLUMN IF NOT EXISTS "Height" integer NULL""",
                $"""ALTER TABLE "{table}" ADD COLUMN IF NOT EXISTS "AspectRatio" text NULL""",
                $"""ALTER TABLE "{table}" ADD COLUMN IF NOT EXISTS "TrueAspectRatio" text NULL""",
                $"""ALTER TABLE "{table}" ADD COLUMN IF NOT EXISTS "SourceConnectionId" uuid NULL""",
                $"""CREATE INDEX IF NOT EXISTS "IX_{table}_IsMissing" ON "{table}" ("IsMissing")""",
                $"""CREATE INDEX IF NOT EXISTS "IX_{table}_AspectRatio" ON "{table}" ("AspectRatio")""",
                $"""CREATE INDEX IF NOT EXISTS "IX_{table}_TrueAspectRatio" ON "{table}" ("TrueAspectRatio")""",
                $"""CREATE INDEX IF NOT EXISTS "IX_{table}_SourceConnectionId" ON "{table}" ("SourceConnectionId")"""
            };

            foreach (var sql in statements)
            {
                try
                {
                    await CatalogSchema.ExecuteAsync(db, sql, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Catalog missing-column ensure skipped for {Sql}", sql);
                }
            }
        }
    }

    private async Task UpgradeTvShowsToEpisodesAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        const string sql = """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'TvShows'
                      AND column_name = 'IsSeries'
                ) THEN
                    INSERT INTO "Episodes" (
                        "Id", "Name", "SortName", "Plot", "OfficialRating", "CommunityRating", "CriticRating",
                        "ProductionYear", "PremiereDate", "RuntimeTicks", "Format", "VideoCodec", "AudioCodec",
                        "Width", "Height", "Path", "JellyfinItemId", "ImdbId", "TmdbId", "TvdbId", "MusicBrainzId",
                        "ProviderIdsJson", "LibraryId", "LibraryName", "PrimaryImagePath", "GenresJson", "StarsJson",
                        "StudiosJson", "TagsJson", "ChaptersJson", "SyncedAt", "IsMissing", "MissingSince",
                        "SeriesId", "SeriesName", "SeasonNumber", "EpisodeNumber"
                    )
                    SELECT
                        "Id", "Name", "SortName", "Plot", "OfficialRating", "CommunityRating", "CriticRating",
                        "ProductionYear", "PremiereDate", "RuntimeTicks", "Format", "VideoCodec", "AudioCodec",
                        "Width", "Height", "Path", "JellyfinItemId", "ImdbId", "TmdbId", "TvdbId", "MusicBrainzId",
                        "ProviderIdsJson", "LibraryId", "LibraryName", "PrimaryImagePath", "GenresJson", "StarsJson",
                        "StudiosJson", "TagsJson", "ChaptersJson", "SyncedAt", "IsMissing", "MissingSince",
                        "SeriesId", "SeriesName", "SeasonNumber", "EpisodeNumber"
                    FROM "TvShows" AS t
                    WHERE COALESCE(t."IsSeries", FALSE) = FALSE
                      AND NOT EXISTS (SELECT 1 FROM "Episodes" AS e WHERE e."Id" = t."Id");

                    DELETE FROM "TvShows" WHERE COALESCE("IsSeries", FALSE) = FALSE;

                    ALTER TABLE "TvShows" DROP COLUMN IF EXISTS "SeasonNumber";
                    ALTER TABLE "TvShows" DROP COLUMN IF EXISTS "EpisodeNumber";
                    ALTER TABLE "TvShows" DROP COLUMN IF EXISTS "SeriesId";
                    ALTER TABLE "TvShows" DROP COLUMN IF EXISTS "SeriesName";
                    ALTER TABLE "TvShows" DROP COLUMN IF EXISTS "IsSeries";
                END IF;
            END $$;
            """;

        try
        {
            await CatalogSchema.ExecuteAsync(db, sql, cancellationToken);
            _logger.LogInformation("TV catalog upgrade checked: episodes live in Episodes; TvShows holds series only");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TV catalog episode table upgrade failed");
        }
    }

    private async Task EnsureMediaServerSchemaAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            """ALTER TABLE "PathMappings" ADD COLUMN IF NOT EXISTS "ConnectionId" uuid NULL""",
            """CREATE INDEX IF NOT EXISTS "IX_PathMappings_ConnectionId" ON "PathMappings" ("ConnectionId")""",
            """
            CREATE TABLE IF NOT EXISTS "MediaServerConnections" (
                "Id" uuid NOT NULL,
                "Kind" integer NOT NULL,
                "Name" text NOT NULL,
                "BaseUrl" text NULL,
                "AccessToken" text NULL,
                "UserId" text NULL,
                "SidecarRoot" text NULL,
                "Enabled" boolean NOT NULL DEFAULT TRUE,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "LastHealthUtc" timestamp with time zone NULL,
                "LastHealthOk" boolean NULL,
                "LastHealthMessage" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                CONSTRAINT "PK_MediaServerConnections" PRIMARY KEY ("Id")
            )
            """,
            """CREATE INDEX IF NOT EXISTS "IX_MediaServerConnections_Kind" ON "MediaServerConnections" ("Kind")""",
            """
            CREATE TABLE IF NOT EXISTS "MediaServerLibraries" (
                "Id" uuid NOT NULL,
                "ConnectionId" uuid NOT NULL,
                "ExternalId" text NOT NULL,
                "Name" text NOT NULL,
                "CollectionType" text NULL,
                "SyncEnabled" boolean NOT NULL DEFAULT TRUE,
                "ItemCount" integer NOT NULL DEFAULT 0,
                "SortOrder" integer NOT NULL DEFAULT 0,
                CONSTRAINT "PK_MediaServerLibraries" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_MediaServerLibraries_Connections" FOREIGN KEY ("ConnectionId")
                    REFERENCES "MediaServerConnections" ("Id") ON DELETE CASCADE
            )
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_MediaServerLibraries_ConnectionId_ExternalId" ON "MediaServerLibraries" ("ConnectionId", "ExternalId")"""
        };

        foreach (var sql in statements)
        {
            try
            {
                await CatalogSchema.ExecuteAsync(db, sql, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Media server schema ensure skipped for {Sql}", sql);
            }
        }
    }
}
