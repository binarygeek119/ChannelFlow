using ChannelFlow.Database;
using ChannelFlow.PluginSdk;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core;

public class EfCoreData : ICoreData
{
    private readonly IServiceProvider _sp;
    private readonly string _dataDir;
    private readonly string _imagesRoot;

    public EfCoreData(IServiceProvider sp, string dataDir)
    {
        _sp = sp;
        _dataDir = Directory.Exists("/config/data") ? "/config/data" : dataDir;
        _imagesRoot = Directory.Exists("/config") ? "/config/images" : Path.Combine(Path.GetDirectoryName(_dataDir)!, "images");
        Directory.CreateDirectory(Path.Combine(_dataDir, "lineups"));
        Directory.CreateDirectory(_imagesRoot);
    }

    public async Task<string?> SaveImageAsync(string category, string id, string sourceUrl, string? apiKey = null)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            if (!string.IsNullOrEmpty(apiKey)) http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Token", apiKey);
            var res = await http.GetAsync(sourceUrl);
            if (!res.IsSuccessStatusCode) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return null;
            var ct = res.Content.Headers.ContentType?.MediaType ?? "";
            var ext = ct switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".jpg"
            };
            var dir = Path.Combine(_imagesRoot, category);
            Directory.CreateDirectory(dir);
            // sanitize id for filename
            var safeId = string.Join("_", id.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"{safeId}{ext}";
            var path = Path.Combine(dir, fileName);
            await File.WriteAllBytesAsync(path, bytes);
            return $"/images/{category}/{fileName}";
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<CoreChannel>> ChannelsAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        return await db.Channels.AsNoTracking().OrderBy(c => c.Number)
            .Select(c => new CoreChannel(c.Id.ToString(), c.Number, c.Name, c.Enabled))
            .ToListAsync();
    }

    public async Task<List<CatalogRow>> CatalogAsync(CatalogFilter filter)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var q = db.MediaItems.AsNoTracking().AsQueryable();
        if (filter.Kinds is { Count: > 0 })
        {
            var kinds = filter.Kinds.Select(k => k.ToLowerInvariant()).ToList();
            q = q.Where(m => kinds.Contains(m.Kind.ToLower()));
        }
        if (filter.Limit is > 0) q = q.Take(filter.Limit.Value);
        var rows = await q.ToListAsync();
        // in-memory genre/tag filtering (case-insensitive substring)
        if (filter.Genres is { Count: > 0 })
        {
            var g = filter.Genres.Select(x => x.ToLowerInvariant()).ToList();
            rows = rows.Where(m => g.Any(x => m.Genres.Any(mg => mg.ToLowerInvariant().Contains(x)))).ToList();
        }
        if (filter.Tags is { Count: > 0 })
        {
            var t = filter.Tags.Select(x => x.ToLowerInvariant()).ToList();
            rows = rows.Where(m => t.Any(x => m.Tags.Any(mt => mt.ToLowerInvariant().Contains(x)))).ToList();
        }
        return rows.Select(m => new CatalogRow(m.MatchKey, m.Kind, m.Title, m.Year, m.RuntimeMs, m.Genres, m.OfficialRating, m.Tags, m.PosterUrl, m.WebUrl, m.SourceKind)).ToList();
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
            var s = await db.Settings.FindAsync("llm");
            if (s is null) return new ChatResponse("", "", new List<string> { "no llm config" });
            var cfg = System.Text.Json.JsonSerializer.Deserialize<LlmConfig>(s.Value);
            if (cfg is null || string.IsNullOrEmpty(cfg.ApiKey) && !cfg.BaseUrl.Contains("localhost") && !cfg.BaseUrl.Contains("127.0.0.1"))
                return new ChatResponse("", "", new List<string> { "llm not configured" });
            var http = new HttpClient();
            var chat = new OpenAiCompatibleChat(http, cfg);
            var content = await chat.CompleteAsync(
                request.Messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "",
                request.Messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "",
                request.Json);
            return new ChatResponse(content, cfg.Provider, new List<string>());
        }
        catch (Exception e)
        {
            return new ChatResponse("", "", new List<string> { e.Message });
        }
    }

    public async Task UpsertMediaItemsAsync(List<MediaItemDto> items)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        foreach (var it in items)
        {
            var existing = await db.MediaItems.FirstOrDefaultAsync(m => m.MatchKey == it.MatchKey);
            if (existing is null)
            {
                db.MediaItems.Add(new MediaItem
                {
                    MatchKey = it.MatchKey,
                    Kind = it.Kind,
                    Title = it.Title,
                    Year = it.Year,
                    RuntimeMs = it.RuntimeMs,
                    Genres = it.Genres ?? new(),
                    OfficialRating = it.OfficialRating,
                    Tags = it.Tags ?? new(),
                    PosterUrl = it.PosterUrl,
                    WebUrl = it.WebUrl,
                    SourceKind = it.SourceKind,
                    Overview = it.Overview,
                    SeriesName = it.SeriesName,
                    SeriesId = it.SeriesId,
                    SeasonName = it.SeasonName,
                    SeasonId = it.SeasonId,
                    ConnectionId = it.ConnectionId,
                    LibraryId = it.LibraryId
                });
            }
            else
            {
                existing.Kind = it.Kind;
                existing.Title = it.Title;
                existing.Year = it.Year;
                existing.RuntimeMs = it.RuntimeMs;
                existing.Genres = it.Genres ?? new();
                existing.OfficialRating = it.OfficialRating;
                existing.Tags = it.Tags ?? new();
                existing.PosterUrl = it.PosterUrl;
                existing.WebUrl = it.WebUrl;
                existing.SourceKind = it.SourceKind;
                existing.Overview = it.Overview;
                existing.SeriesName = it.SeriesName;
                existing.SeriesId = it.SeriesId;
                existing.SeasonName = it.SeasonName;
                existing.SeasonId = it.SeasonId;
                existing.ConnectionId = it.ConnectionId;
                existing.LibraryId = it.LibraryId;
            }
        }
        await db.SaveChangesAsync();
    }

    public async Task RemoveMediaItemsAsync(string connectionId, string libraryId)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var rows = db.MediaItems.Where(m => m.ConnectionId == connectionId && m.LibraryId == libraryId);
        db.MediaItems.RemoveRange(rows);
        await db.SaveChangesAsync();
    }

    public async Task GeneratePlayoutAsync(string channelId)
    {
        channelId = channelId.ToLowerInvariant();
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id.ToString().ToUpper() == channelId.ToUpper());
        var lineupPath = Path.Combine(_dataDir, "lineups", channelId + ".json");
        if (!File.Exists(lineupPath)) return;
        var lineupJson = await File.ReadAllTextAsync(lineupPath);
        var lineup = System.Text.Json.JsonSerializer.Deserialize<List<LineupSlot>>(lineupJson) ?? new();

        var playoutDir = Path.Combine(_dataDir, "playout");
        Directory.CreateDirectory(playoutDir);
        var channelNumber = channel?.Number ?? 0;
        var channelName = channel?.Name ?? channelId;

        var items = new List<object>();
        foreach (var slot in lineup)
        {
            var cand = slot.Candidates?.FirstOrDefault();
            items.Add(new
            {
                slotIndex = slot.SlotIndex,
                spanSlots = slot.SpanSlots,
                isRerun = slot.IsRerun,
                matchKey = cand?.MatchKey,
                title = cand?.Title,
                kind = cand?.Kind,
                weight = cand?.Weight
            });
        }

        var doc = new
        {
            channelId = channelId,
            channelNumber = channelNumber,
            channelName = channelName,
            generatedAt = DateTimeOffset.UtcNow,
            slotMinutes = 30,
            items = items
        };
        var outPath = Path.Combine(playoutDir, channelId + ".json");
        var json = System.Text.Json.JsonSerializer.Serialize(doc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outPath, json);
    }

    public async Task<List<PersonCreditDto>> GetCreditsForMediaAsync(string mediaMatchKey)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var credits = await db.PersonCredits.AsNoTracking().Where(c => c.MediaMatchKey == mediaMatchKey).ToListAsync();
        var personIds = credits.Select(c => c.PersonId).Distinct().ToList();
        var people = await db.People.Where(p => personIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        return credits.Select(c => new PersonCreditDto(c.Id, c.PersonId, c.MediaMatchKey, c.Role, c.Character,
            people.TryGetValue(c.PersonId, out var p) ? p.ImagePath : null)).ToList();
    }

    public async Task<PersonCreditDto> AddCreditAsync(PersonCreditDto credit)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var entity = new PersonCredit
        {
            Id = credit.Id == Guid.Empty ? Guid.NewGuid() : credit.Id,
            PersonId = credit.PersonId,
            MediaMatchKey = credit.MediaMatchKey,
            Role = credit.Role,
            Character = credit.Character
        };
        db.PersonCredits.Add(entity);
        await db.SaveChangesAsync();
        var person = await db.People.FindAsync(entity.PersonId);
        return new PersonCreditDto(entity.Id, entity.PersonId, entity.MediaMatchKey, entity.Role, entity.Character, person?.ImagePath);
    }

    public async Task RemoveCreditAsync(Guid creditId)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var c = await db.PersonCredits.FindAsync(creditId);
        if (c is not null) { db.PersonCredits.Remove(c); await db.SaveChangesAsync(); }
    }

    public async Task<List<PersonDto>> UpsertPeopleAsync(List<PersonDto> people)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var result = new List<PersonDto>();
        foreach (var p in people)
        {
            var existing = await db.People.FirstOrDefaultAsync(x => x.Name == p.Name);
            if (existing is null)
            {
                var person = new Person { Name = p.Name, ImagePath = p.ImagePath };
                db.People.Add(person);
                await db.SaveChangesAsync();
                result.Add(new PersonDto(person.Id, person.Name, person.ImagePath));
            }
            else
            {
                if (p.ImagePath is not null && existing.ImagePath is null)
                {
                    existing.ImagePath = p.ImagePath;
                    await db.SaveChangesAsync();
                }
                result.Add(new PersonDto(existing.Id, existing.Name, existing.ImagePath));
            }
        }
        return result;
    }

    public async Task LinkCreditsAsync(string mediaMatchKey, List<PersonCreditDto> credits)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        // Remove existing credits for this media item
        var existing = db.PersonCredits.Where(c => c.MediaMatchKey == mediaMatchKey);
        db.PersonCredits.RemoveRange(existing);
        foreach (var cr in credits)
        {
            db.PersonCredits.Add(new PersonCredit
            {
                Id = cr.Id == Guid.Empty ? Guid.NewGuid() : cr.Id,
                PersonId = cr.PersonId,
                MediaMatchKey = mediaMatchKey,
                Role = cr.Role,
                Character = cr.Character
            });
        }
        await db.SaveChangesAsync();
    }

    public Task FileForAsync(string matchKey) => Task.CompletedTask;

    public async Task SetChannelGuideAsync(string channelId, List<GuideProgram> programs)
    {
        channelId = channelId.ToUpperInvariant();
        // Merge by local date: remove existing programs whose date matches incoming dates
        var incomingDates = programs.Select(p =>
        {
            if (DateTimeOffset.TryParse(p.Start, out var dto)) return dto.Date;
            return DateTime.MinValue.Date;
        }).Distinct().ToHashSet();

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelFlowDbContext>();
        var existing = await db.GuidePrograms.Where(g => g.ChannelId == channelId).ToListAsync();
        foreach (var e in existing)
        {
            if (DateTimeOffset.TryParse(e.Start, out var dto) && incomingDates.Contains(dto.Date))
                db.GuidePrograms.Remove(e);
        }
        foreach (var p in programs)
        {
            db.GuidePrograms.Add(new GuideProgramRow
            {
                ChannelId = channelId,
                ProgramId = p.Id,
                Start = p.Start,
                Finish = p.Finish,
                Title = p.Title,
                Description = p.Description,
                PosterUrl = p.PosterUrl
            });
        }
        await db.SaveChangesAsync();
    }

    public async Task SetChannelLineupAsync(string channelId, List<LineupSlot> slots)
    {
        channelId = channelId.ToLowerInvariant();
        var path = Path.Combine(_dataDir, "lineups", channelId + ".json");
        var json = System.Text.Json.JsonSerializer.Serialize(slots, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }
}
