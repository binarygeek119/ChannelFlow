using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Api;

/// <summary>
/// AI lineup generation settings and actions.
/// </summary>
[ApiController]
[Route("api/ai")]
[Authorize(Policy = "admin")]
public class AiController : ControllerBase
{
    private readonly AiLineupGeneratorService _generator;
    private readonly AiChannelAutoApplyService _autoApply;
    private readonly LlmClientService _llm;
    private readonly FinTvDbContext _db;
    private readonly LineupGeneratorService _playoutGenerator;
    private readonly PlayoutBuilderService _playoutBuilder;
    private readonly WeatherGuideMetadataService _weatherGuide;
    private readonly AiChannelGenerateJobService _channelGenerateJobs;
    private readonly ChannelCatalogPoolService _pool;
    private readonly ChannelPrimetimeService _primetime;

    public AiController(
        AiLineupGeneratorService generator,
        AiChannelAutoApplyService autoApply,
        LlmClientService llm,
        FinTvDbContext db,
        LineupGeneratorService playoutGenerator,
        PlayoutBuilderService playoutBuilder,
        WeatherGuideMetadataService weatherGuide,
        AiChannelGenerateJobService channelGenerateJobs,
        ChannelCatalogPoolService pool,
        ChannelPrimetimeService primetime)
    {
        _generator = generator;
        _autoApply = autoApply;
        _llm = llm;
        _db = db;
        _playoutGenerator = playoutGenerator;
        _playoutBuilder = playoutBuilder;
        _weatherGuide = weatherGuide;
        _channelGenerateJobs = channelGenerateJobs;
        _pool = pool;
        _primetime = primetime;
    }

    [HttpGet("settings")]
    public ActionResult<object> GetSettings()
        => Ok(BuildSettingsResponse());

    [HttpPut("settings")]
    public ActionResult<object> UpdateSettings([FromBody] AiSettingsRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        var plugin = FinTvRuntime.Current ?? throw new InvalidOperationException("ChannelFlow plugin not initialized.");
        var ai = plugin.Configuration.Ai;

        if (request.Enabled.HasValue)
        {
            ai.Enabled = request.Enabled.Value;
        }

        if (request.DefaultProvider.HasValue)
        {
            ai.DefaultProvider = request.DefaultProvider.Value;
        }

        if (request.OpenAiModel is not null)
        {
            ai.OpenAiModel = request.OpenAiModel;
        }

        if (request.VeniceModel is not null)
        {
            ai.VeniceModel = request.VeniceModel;
        }

        if (request.TtsModel is not null)
        {
            ai.TtsModel = request.TtsModel.Trim();
        }

        if (request.TtsVoice is not null)
        {
            ai.TtsVoice = string.IsNullOrWhiteSpace(request.TtsVoice) ? "nova" : request.TtsVoice.Trim();
        }

        if (request.MaxCatalogItemsInPrompt.HasValue)
        {
            ai.MaxCatalogItemsInPrompt = Math.Clamp(request.MaxCatalogItemsInPrompt.Value, 10, 1000);
        }

        if (request.AutoApplyOnChannelAdd.HasValue)
        {
            ai.AutoApplyOnChannelAdd = request.AutoApplyOnChannelAdd.Value;
        }

        if (request.AutoApplyToAllChannelsOnSave.HasValue)
        {
            ai.AutoApplyToAllChannelsOnSave = request.AutoApplyToAllChannelsOnSave.Value;
        }

        if (request.SimulateOriginalBroadcasting.HasValue)
        {
            ai.SimulateOriginalBroadcasting = request.SimulateOriginalBroadcasting.Value;
        }

        ai.OpenAiApiKey = CoalesceApiKey(request.OpenAiApiKey, ai.OpenAiApiKey, plugin.Configuration.ApiKey);
        ai.VeniceApiKey = CoalesceApiKey(request.VeniceApiKey, ai.VeniceApiKey, plugin.Configuration.ApiKey);

        plugin.SaveConfiguration();

        object? applyAllSummary = null;
        if (ai.Enabled && ai.AutoApplyToAllChannelsOnSave)
        {
            _autoApply.QueueApplyToAllEligibleChannels();
            applyAllSummary = new { queued = true };
        }

        return Ok(new
        {
            settings = BuildSettingsResponse(),
            applyAll = applyAllSummary
        });
    }

    private static object BuildSettingsResponse()
    {
        var ai = FinTvRuntime.Current?.Configuration.Ai ?? new AiSettings();
        return new
        {
            enabled = ai.Enabled,
            defaultProvider = (int)ai.DefaultProvider,
            openAiModel = ai.OpenAiModel,
            veniceModel = ai.VeniceModel,
            ttsModel = ai.TtsModel ?? "",
            ttsVoice = string.IsNullOrWhiteSpace(ai.TtsVoice) ? "nova" : ai.TtsVoice,
            maxCatalogItemsInPrompt = ai.MaxCatalogItemsInPrompt,
            hasOpenAiApiKey = !string.IsNullOrWhiteSpace(ai.OpenAiApiKey),
            hasVeniceApiKey = !string.IsNullOrWhiteSpace(ai.VeniceApiKey),
            openAiApiKeyMasked = MaskKey(ai.OpenAiApiKey),
            veniceApiKeyMasked = MaskKey(ai.VeniceApiKey),
            autoApplyOnChannelAdd = ai.AutoApplyOnChannelAdd,
            autoApplyToAllChannelsOnSave = ai.AutoApplyToAllChannelsOnSave,
            simulateOriginalBroadcasting = ai.SimulateOriginalBroadcasting
        };
    }

    [HttpPost("settings/test")]
    public async Task<IActionResult> TestSettings([FromBody] AiTestSettingsRequest? request, CancellationToken cancellationToken)
    {
        var plugin = FinTvRuntime.Current ?? throw new InvalidOperationException("ChannelFlow plugin not initialized.");
        var provider = ResolveTestProvider(request, plugin.Configuration.Ai);

        try
        {
            await _llm.TestConnectionAsync(
                provider,
                CoalesceApiKey(request?.OpenAiApiKey, null, plugin.Configuration.ApiKey),
                CoalesceApiKey(request?.VeniceApiKey, null, plugin.Configuration.ApiKey),
                cancellationToken);
            return Ok(new { ok = true, provider = provider.ToString() });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message, provider = provider.ToString() });
        }
        catch (HttpRequestException ex)
        {
            return BadRequest(new
            {
                message = $"Could not reach {provider} API. Check Jellyfin server internet access and DNS.",
                provider = provider.ToString(),
                detail = ex.Message
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Connection test failed: {ex.Message}", provider = provider.ToString() });
        }
    }

    private static AiProvider ResolveTestProvider(AiTestSettingsRequest? request, AiSettings settings)
    {
        if (request?.ProviderId is >= 0 and <= 1)
        {
            return (AiProvider)request.ProviderId.Value;
        }

        return request?.Provider ?? settings.DefaultProvider;
    }

    [HttpGet("channels")]
    public async Task<ActionResult<object>> GetChannels(CancellationToken cancellationToken)
    {
        var channels = await _db.Channels
            .AsNoTracking()
            .OrderBy(c => c.Number)
            .ToListAsync(cancellationToken);

        var slotCounts = await _db.Lineups
            .Where(l => l.IsDefault)
            .Select(l => new { l.ChannelId, Count = l.Slots.Count(s => s.Candidates.Count > 0) })
            .ToDictionaryAsync(x => x.ChannelId, x => x.Count, cancellationToken);
        var poolCounts = await _db.ChannelCatalogPool
            .GroupBy(row => row.ChannelId)
            .Select(group => new { ChannelId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.ChannelId, x => x.Count, cancellationToken);

        return Ok(channels
            .Where(c => c.ContentType != ChannelContentType.Weather && c.ContentType != ChannelContentType.News)
            .Where(c => !ChannelAiRules.IsExcludedFromAi(ChannelAiRules.ExtractLibraryTag(c.FilterJson)))
            .Select(c =>
            {
                var tag = ChannelAiRules.ExtractLibraryTag(c.FilterJson);
                return new
                {
                    id = c.Id,
                    number = c.Number,
                    name = c.Name,
                    libraryTag = tag,
                    catalogMode = (int)ChannelAiRules.ResolveCatalogMode(c),
                    aiFineTunePrompt = c.AiFineTunePrompt ?? string.Empty,
                    aiPlayoutTemplateId = c.AiPlayoutTemplateId ?? AiPlayoutTemplates.NoneId,
                    aiRuleBrief = ChannelAiRules.GetBrief(tag),
                    filledSlots = slotCounts.TryGetValue(c.Id, out var count) ? count : 0,
                    poolCount = poolCounts.TryGetValue(c.Id, out var pool) ? pool : 0,
                    primetimeAssignment = ChannelAiRules.IsPrimetimeAssignmentLibraryTag(tag)
                };
            }));
    }

    [HttpGet("playout-templates")]
    public ActionResult<object> GetPlayoutTemplates()
    {
        return Ok(AiPlayoutTemplates.ListAll().Select(t => new
        {
            id = t.Id,
            name = t.Name,
            description = t.Description,
            dayparts = t.Dayparts.Select(d => new
            {
                name = d.Name,
                slotRange = d.FormatSlotRange(),
                brief = d.Brief
            })
        }));
    }

    [HttpPut("channels/{channelId:guid}/playout-template")]
    public async Task<IActionResult> UpdatePlayoutTemplate(
        Guid channelId,
        [FromBody] AiPlayoutTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        channel.AiPlayoutTemplateId = string.IsNullOrWhiteSpace(request.AiPlayoutTemplateId)
            ? AiPlayoutTemplates.NoneId
            : request.AiPlayoutTemplateId.Trim();

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { ok = true });
    }

    [HttpPut("channels/{channelId:guid}/fine-tune")]
    public async Task<IActionResult> UpdateFineTune(
        Guid channelId,
        [FromBody] AiChannelSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        if (request.AiFineTunePrompt is not null)
        {
            channel.AiFineTunePrompt = request.AiFineTunePrompt;
        }

        if (request.CatalogMode.HasValue)
        {
            channel.CatalogMode = request.CatalogMode.Value;
        }

        if (request.AiPlayoutTemplateId is not null)
        {
            channel.AiPlayoutTemplateId = string.IsNullOrWhiteSpace(request.AiPlayoutTemplateId)
                ? AiPlayoutTemplates.NoneId
                : request.AiPlayoutTemplateId.Trim();
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { ok = true });
    }

    [HttpPost("channels/{channelId:guid}/generate")]
    public ActionResult<object> Generate(
        Guid channelId,
        [FromBody] AiGenerateRequest? request)
    {
        if (FinTvRuntime.Current?.Configuration.Ai.Enabled != true)
        {
            return BadRequest(new { message = "AI lineup generation is disabled." });
        }

        if (!_channelGenerateJobs.TryQueue(channelId, request?.Provider))
        {
            return Ok(new
            {
                queued = false,
                alreadyRunning = true,
                job = _channelGenerateJobs.BuildStatus(channelId)
            });
        }

        return Ok(new
        {
            queued = true,
            job = _channelGenerateJobs.BuildStatus(channelId)
        });
    }

    [HttpGet("channels/{channelId:guid}/generate/status")]
    public ActionResult<object> GetGenerateStatus(Guid channelId)
        => Ok(_channelGenerateJobs.BuildStatus(channelId));

    [HttpDelete("channels/{channelId:guid}/pool")]
    public async Task<IActionResult> ClearChannelPool(Guid channelId, CancellationToken cancellationToken)
    {
        var exists = await _db.Channels.AnyAsync(c => c.Id == channelId, cancellationToken);
        if (!exists)
        {
            return NotFound();
        }

        await _pool.ClearAsync(channelId, cancellationToken);
        return Ok(new { ok = true, message = "AI pool cleared. Next Generate will rebuild it." });
    }

    [HttpGet("channels/{channelId:guid}/primetime-shows")]
    public async Task<IActionResult> GetPrimetimeShows(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        if (!ChannelAiRules.IsPrimetimeAssignmentChannel(channel))
        {
            return BadRequest(new { message = "This channel does not use primetime show assignments." });
        }

        var shows = await _primetime.ListEligibleShowsAsync(channel, cancellationToken);
        return Ok(new { shows });
    }

    [HttpGet("channels/{channelId:guid}/primetime")]
    public async Task<IActionResult> GetPrimetime(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        if (!ChannelAiRules.IsPrimetimeAssignmentChannel(channel))
        {
            return BadRequest(new { message = "This channel does not use primetime show assignments." });
        }

        var slots = await _primetime.LoadAsync(channelId, cancellationToken);
        return Ok(_primetime.BuildResponse(slots));
    }

    [HttpPut("channels/{channelId:guid}/primetime")]
    public async Task<IActionResult> UpdatePrimetime(
        Guid channelId,
        [FromBody] ChannelPrimetimePutRequest? request,
        CancellationToken cancellationToken)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        if (!ChannelAiRules.IsPrimetimeAssignmentChannel(channel))
        {
            return BadRequest(new { message = "This channel does not use primetime show assignments." });
        }

        await _primetime.SaveAsync(channelId, request?.Slots ?? [], cancellationToken);
        var slots = await _primetime.LoadAsync(channelId, cancellationToken);
        return Ok(_primetime.BuildResponse(slots));
    }

    [HttpDelete("pools")]
    public async Task<IActionResult> ClearAllPools(CancellationToken cancellationToken)
    {
        await _pool.ClearAllAsync(cancellationToken);
        return Ok(new { ok = true, message = "AI pool cleared. Next Generate will rebuild it." });
    }

    [HttpGet("generate/queue")]
    public ActionResult<object> GetQueueStatus()
        => Ok(_channelGenerateJobs.BuildQueueStatus());

    [HttpPost("channels/{channelId:guid}/apply")]
    public async Task<IActionResult> Apply(
        Guid channelId,
        [FromBody] AiApplyLineupRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var rebuildPlayout = request?.RebuildPlayout ?? true;
            using (await ChannelApplyLocks.AcquireAsync(channelId, cancellationToken))
            {
                await _generator.ApplyAsync(
                    channelId,
                    request?.Slots ?? new List<LineupSlotDto>(),
                    rebuildPlayout: false,
                    _playoutGenerator,
                    cancellationToken);
            }

            if (rebuildPlayout)
            {
                _playoutBuilder.QueueRebuildChannel(channelId);
            }

            return Ok(new { ok = true, rebuildQueued = rebuildPlayout });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("generate-all")]
    public ActionResult<object> GenerateAll()
    {
        if (FinTvRuntime.Current?.Configuration.Ai.Enabled != true)
        {
            return BadRequest(new { message = "AI lineup generation is disabled." });
        }

        _channelGenerateJobs.QueueGenerateAll();
        return Ok(new { queued = true, job = _channelGenerateJobs.BuildGenerateAllStatus() });
    }

    [HttpGet("generate-all/status")]
    public ActionResult<object> GetGenerateAllStatus()
        => Ok(_channelGenerateJobs.BuildGenerateAllStatus());

    [HttpPost("generate-all/cancel")]
    public ActionResult<object> CancelGenerateAll()
    {
        var cancelled = _channelGenerateJobs.Cancel();
        return Ok(new { cancelled, job = _channelGenerateJobs.BuildGenerateAllStatus() });
    }

    [HttpGet("weather-guide-cache/status")]
    public async Task<ActionResult<object>> GetWeatherGuideCacheStatus(CancellationToken cancellationToken)
        => Ok(await _weatherGuide.BuildCacheStatusAsync(cancellationToken));

    [HttpPost("weather-guide-cache/generate")]
    public async Task<IActionResult> GenerateWeatherGuideCache(
        [FromBody] WeatherGuideCacheGenerateRequest? request,
        CancellationToken cancellationToken)
    {
        if (_weatherGuide.IsGenerating)
        {
            return Ok(new
            {
                queued = false,
                alreadyRunning = true,
                status = await _weatherGuide.BuildCacheStatusAsync(cancellationToken)
            });
        }

        _weatherGuide.QueueGenerateCache(request?.Force != false);

        return Accepted(new
        {
            queued = true,
            status = await _weatherGuide.BuildCacheStatusAsync(cancellationToken)
        });
    }

    [HttpDelete("weather-guide-cache")]
    public ActionResult<object> ClearWeatherGuideCache()
    {
        var cleared = _weatherGuide.ClearCache();
        return Ok(new { cleared });
    }

    private static string? CoalesceApiKey(string? incoming, string? current, string? pluginApiKey)
    {
        var next = NormalizeIncomingApiKey(incoming);
        if (string.IsNullOrWhiteSpace(next))
        {
            return current;
        }

        if (string.Equals(next, current, StringComparison.Ordinal)
            || string.Equals(next, MaskKey(current), StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(pluginApiKey)
                && string.Equals(next, pluginApiKey, StringComparison.Ordinal)))
        {
            return current;
        }

        return next;
    }

    private static string? NormalizeIncomingApiKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().Trim('"', '\'');
        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].Trim();
        }

        if (LooksLikeMaskedOrPlaceholder(trimmed))
        {
            return null;
        }

        return trimmed;
    }

    private static bool LooksLikeMaskedOrPlaceholder(string key)
    {
        if (key is "****" or "*" or "•" or "••••")
        {
            return true;
        }

        if (key.Contains("...", StringComparison.Ordinal) && key.Length <= 16)
        {
            return true;
        }

        return key.All(character => character is '*' or '•' or '.');
    }

    private static string MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        if (key.Length <= 8)
        {
            return "****";
        }

        return key[..4] + "..." + key[^4..];
    }
}

public class AiSettingsRequest
{
    public bool? Enabled { get; set; }

    public AiProvider? DefaultProvider { get; set; }

    public string? OpenAiApiKey { get; set; }

    public string? OpenAiModel { get; set; }

    public string? VeniceApiKey { get; set; }

    public string? VeniceModel { get; set; }

    public string? TtsModel { get; set; }

    public string? TtsVoice { get; set; }

    public int? MaxCatalogItemsInPrompt { get; set; }

    public bool? AutoApplyOnChannelAdd { get; set; }

    public bool? AutoApplyToAllChannelsOnSave { get; set; }

    public bool? SimulateOriginalBroadcasting { get; set; }
}

public class AiTestSettingsRequest
{
    /// <summary>
    /// Provider id: 0 = OpenAI, 1 = Venice. Preferred over <see cref="Provider"/> for Jellyfin JSON binding.
    /// </summary>
    public int? ProviderId { get; set; }

    public AiProvider? Provider { get; set; }

    public string? OpenAiApiKey { get; set; }

    public string? VeniceApiKey { get; set; }
}

public class AiChannelSettingsRequest
{
    public string? AiFineTunePrompt { get; set; }

    public ChannelCatalogMode? CatalogMode { get; set; }

    public string? AiPlayoutTemplateId { get; set; }
}

public class AiPlayoutTemplateRequest
{
    public string? AiPlayoutTemplateId { get; set; }
}

public class AiGenerateRequest
{
    public AiProvider? Provider { get; set; }
}

public class AiApplyLineupRequest
{
    public List<LineupSlotDto>? Slots { get; set; }

    public bool RebuildPlayout { get; set; }
}

public class WeatherGuideCacheGenerateRequest
{
    public bool Force { get; set; }
}

public class ChannelPrimetimePutRequest
{
    public List<ChannelPrimetimeSlotRequest>? Slots { get; set; }
}
