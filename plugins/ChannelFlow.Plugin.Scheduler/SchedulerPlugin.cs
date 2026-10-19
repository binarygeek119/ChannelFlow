using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using ChannelFlow.PluginSdk;

namespace ChannelFlow.Plugin.Scheduler;

public class TimeOfDayBlock
{
    public string Name { get; set; } = "";
    public int StartHour { get; set; } = 0;
    public int EndHour { get; set; } = 24;
    public List<string> Kinds { get; set; } = new();
}

public class SpecialEntry
{
    public string Title { get; set; } = "";
    public int StartHour { get; set; } = 0;
    public int StartMinute { get; set; } = 0;
    public int DurationMinutes { get; set; } = 30;
    public List<string> Kinds { get; set; } = new();
}

public class SchedulerConfig
{
    public List<string> Kinds { get; set; } = new() { "movie", "series", "episode" };
    public List<string> Genres { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public int HorizonDays { get; set; } = 7;
    public int SlotMinutes { get; set; } = 30;
    // Special scheduling rules (v2 ports)
    public List<SpecialEntry> SpecialEntries { get; set; } = new(); // fixed programs at specific times (e.g. FlowWire News)
    public bool OpenSwimAlternation { get; set; } = false; // alternate content pools every 2 hours
    public int AlternationBlockHours { get; set; } = 2;
    public List<string> AlternationKindsA { get; set; } = new() { "movie" };
    public List<string> AlternationKindsB { get; set; } = new() { "series", "episode" };
    public List<string> SourceRestrictionKinds { get; set; } = new(); // if set, only these kinds allowed (e.g. Past Tense News)
    public List<TimeOfDayBlock> TimeOfDayBlocks { get; set; } = new(); // time-of-day programming blocks
    public bool RerunDetection { get; set; } = true; // mark repeats as reruns
}

public class SchedulerPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.scheduler", "Scheduler", "3.0.0", "3.0.0", Category: "scheduler");

    private PluginApi? _api;
    private SchedulerConfig _config = new();
    private readonly object _lock = new();
    private bool _running;
    private string _status = "idle";
    private int _current, _total;
    private CancellationTokenSource? _cts;

    public Task OnLoadAsync(PluginApi api) { _api = api; return Task.CompletedTask; }
    public Task OnEnableAsync() => Task.CompletedTask;
    public Task OnDisableAsync() => Task.CompletedTask;
    public void OnUnload() { _cts?.Cancel(); }
    public async Task OnConfigAsync(JsonElement config)
    {
        try { _config = JsonSerializer.Deserialize<SchedulerConfig>(config, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? _config; }
        catch { }
        await Task.CompletedTask;
    }

    public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/status", () => new { status = _status, running = _running, current = _current, total = _total });
        endpoints.MapPost("/build", () =>
        {
            if (_running) return Results.Conflict(new { error = "already running" });
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => RunBuildAsync(_cts.Token));
            return Results.Ok(new { started = true });
        });
        endpoints.MapPost("/cancel", () =>
        {
            _cts?.Cancel();
            return Results.Ok(new { cancelled = true });
        });
        endpoints.MapGet("/config", () => Results.Ok(_config));
        endpoints.MapPost("/config", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                var cfg = System.Text.Json.JsonSerializer.Deserialize<SchedulerConfig>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg is not null) _config = cfg;
                return Results.Ok(new { ok = true });
            }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });
    }

    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, _status);

    public IMediaSource? MediaSource => null;

    private async Task RunBuildAsync(CancellationToken ct)
    {
        lock (_lock) { _running = true; _status = "running"; }
        try
        {
            var core = _api!.Core;
            var channels = await core.ChannelsAsync();
            _total = channels.Count;
            _current = 0;
            var slotMs = _config.SlotMinutes * 60 * 1000L;
            var slotsPerDay = (24 * 60) / _config.SlotMinutes;
            var totalSlots = slotsPerDay * _config.HorizonDays;

            foreach (var ch in channels)
            {
                if (ct.IsCancellationRequested) break;
                _api.Tasks?.Report("scheduler.build", "Scheduler Build", ch.Name, _current, _total, "running", $"Building channel {_current + 1} of {_total}");

                var kinds = _config.SourceRestrictionKinds.Count > 0 ? _config.SourceRestrictionKinds : _config.Kinds;
                var catalog = await core.CatalogAsync(new CatalogFilter(kinds, _config.Genres, _config.Tags, null));
                if (catalog.Count == 0)
                {
                    _current++;
                    continue;
                }

                // Try LLM planning; fall back to round-robin
                List<string> pickedKeys = new();
                try
                {
                    var sample = catalog.Take(50).ToList();
                    var itemLines = string.Join("\n", sample.Select((it, i) => $"{i}: {it.Title} ({it.Kind}, {it.Year}) genres=[{string.Join(",", it.Genres)}]"));
                    var systemPrompt = "You are a TV channel scheduler. Given a catalog of items and a number of 30-minute slots, return a JSON object with a single key \"slots\" whose value is an array of catalog indices (integers) of length equal to the number of slots. Vary content, avoid long repeats. Respect time of day: morning (6-12) news/talk, afternoon (12-17) light entertainment, evening prime (17-22) movies/drama, late night (22-6) reruns/quiet. Mark reruns by reusing indices. Return only JSON.";
                    var userPrompt = $"Catalog ({sample.Count} items):\n{itemLines}\n\nNumber of slots: {totalSlots}. Return JSON.";
                    var resp = await core.ChatAsync(new ChatRequest(new List<ChatMessage> { new("system", systemPrompt), new("user", userPrompt) }, _config is null ? 0.7 : 0.7, true, null));
                    if (!string.IsNullOrWhiteSpace(resp.Content))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(resp.Content);
                        if (doc.RootElement.TryGetProperty("slots", out var arr))
                        {
                            foreach (var el in arr.EnumerateArray())
                            {
                                var idx = el.GetInt32();
                                if (idx >= 0 && idx < sample.Count)
                                    pickedKeys.Add(sample[idx].MatchKey);
                                else
                                    pickedKeys.Add(catalog[0].MatchKey);
                            }
                        }
                    }
                }
                catch { pickedKeys.Clear(); }

                if (pickedKeys.Count != totalSlots)
                {
                    // deterministic fallback with time-of-day blocks
                    pickedKeys.Clear();
                    for (int slot = 0; slot < totalSlots; slot++)
                    {
                        var hour = (slot * _config.SlotMinutes / 60) % 24;
                        var block = _config.TimeOfDayBlocks.FirstOrDefault(b => hour >= b.StartHour && hour < b.EndHour);
                        List<CatalogRow> pool = catalog;
                        if (block is not null && block.Kinds.Count > 0)
                        {
                            var filtered = catalog.Where(c => block.Kinds.Contains(c.Kind)).ToList();
                            if (filtered.Count > 0) pool = filtered;
                        }
                        pickedKeys.Add(pool[slot % pool.Count].MatchKey);
                    }
                }

                var lookup = catalog.ToDictionary(c => c.MatchKey);
                var usedKeys = new HashSet<string>();

                // Build guide programs
                var baseDate = DateTime.UtcNow.Date;
                var programs = new List<GuideProgram>();
                var lineupSlots = new List<LineupSlot>();

                // OpenSwim alternation: override pickedKeys pool per block
                if (_config?.OpenSwimAlternation == true)
                {
                    var slotsPerBlock = (_config.AlternationBlockHours * 60) / _config.SlotMinutes;
                    for (int slot = 0; slot < totalSlots; slot++)
                    {
                        var block = (slot / slotsPerBlock) % 2;
                        var poolKinds = block == 0 ? _config.AlternationKindsA : _config.AlternationKindsB;
                        var pool = catalog.Where(c => poolKinds.Contains(c.Kind)).ToList();
                        if (pool.Count == 0) pool = catalog;
                        if (slot >= pickedKeys.Count) pickedKeys.Add(pool[slot % pool.Count].MatchKey);
                        else pickedKeys[slot] = pool[slot % pool.Count].MatchKey;
                    }
                }

                // Special entries: build a set of slots occupied by fixed programs
                var specialSlots = new Dictionary<int, SpecialEntry>();
                foreach (var se in _config.SpecialEntries)
                {
                    var slotOfDay = (se.StartHour * 60 + se.StartMinute) / _config.SlotMinutes;
                    var spanSlots = Math.Max(1, se.DurationMinutes / _config.SlotMinutes);
                    for (int day = 0; day < _config.HorizonDays; day++)
                    {
                        for (int ss = 0; ss < spanSlots; ss++)
                        {
                            var globalSlot = day * slotsPerDay + slotOfDay + ss;
                            if (globalSlot >= 0 && globalSlot < totalSlots)
                                specialSlots[globalSlot] = se;
                        }
                    }
                }

                for (int slot = 0; slot < totalSlots; slot++)
                {
                    if (ct.IsCancellationRequested) break;
                    var start = baseDate.AddMinutes(slot * _config.SlotMinutes);
                    var finish = start.AddMinutes(_config.SlotMinutes);

                    GuideProgram gp;
                    LineupSlot ls;
                    if (specialSlots.TryGetValue(slot, out var se))
                    {
                        // Fixed special entry
                        gp = new GuideProgram(
                            Id: $"special-{ch.Id}-{slot}",
                            Start: start.ToString("o"),
                            Finish: finish.ToString("o"),
                            Title: se.Title,
                            Description: "Special programming",
                            SubTitle: null,
                            Episode: null,
                            Categories: se.Kinds,
                            Year: null,
                            Rating: null,
                            PosterUrl: null);
                        ls = new LineupSlot(slot, 1, false, new List<SlotCandidate> { new SlotCandidate("special", $"special:{se.Title}", se.Title, 100) });
                    }
                    else
                    {
                        var key = pickedKeys[slot];
                        var item = lookup.TryGetValue(key, out var it) ? it : catalog[slot % catalog.Count];
                        gp = new GuideProgram(
                            Id: $"sched-{ch.Id}-{slot}",
                            Start: start.ToString("o"),
                            Finish: finish.ToString("o"),
                            Title: item.Title,
                            Description: null,
                            SubTitle: null,
                            Episode: null,
                            Categories: item.Genres,
                            Year: item.Year,
                            Rating: item.OfficialRating,
                            PosterUrl: null);
                        var isRerun = _config.RerunDetection && usedKeys.Contains(item.MatchKey);
                        if (_config.RerunDetection) usedKeys.Add(item.MatchKey);
                        ls = new LineupSlot(slot, 1, isRerun, new List<SlotCandidate> { new SlotCandidate(item.Kind, item.MatchKey, item.Title, 100) });
                    }
                    programs.Add(gp);
                    lineupSlots.Add(ls);


                }

                await core.SetChannelGuideAsync(ch.Id, programs);
                await core.SetChannelLineupAsync(ch.Id, lineupSlots);
                try { await core.GeneratePlayoutAsync(ch.Id); } catch { }

                _current++;
            }

            _status = ct.IsCancellationRequested ? "cancelled" : "completed";
            _api?.Tasks?.Report("scheduler.build", "Scheduler Build", null, _total, _total, _status == "completed" ? "done" : "failed", _status);
        }
        catch (Exception e)
        {
            _status = "error: " + e.Message;
            _api?.Logger.Error(e.ToString());
        }
        finally
        {
            lock (_lock) { _running = false; }
        }
    }
}
