using System.Text.Json;
using ChannelFlow.PluginSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ChannelFlow.Plugin.HolidayChannel;

public class Holiday
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DateKind { get; set; } = "fixed"; // fixed, easter, thanksgiving
    public int ObservanceMonth { get; set; }
    public int ObservanceDay { get; set; }
    public int DaysBefore { get; set; } = 30;
    public int DaysAfter { get; set; } = 0;
    public bool BleedIntoPriorMonth { get; set; }
    public string Logo { get; set; } = "";
    public string? EarlyLogo { get; set; }
    public int? EarlyLogoUntilDay { get; set; }
    public List<string> Keywords { get; set; } = new();
}

public static class HolidayCalendar
{
    public static readonly List<Holiday> Holidays = new()
    {
        new() { Id = "valentines", Name = "Valentine's Day", DateKind = "fixed", ObservanceMonth = 2, ObservanceDay = 14, DaysBefore = 25, DaysAfter = 0, Logo = "logos/valentines.png", Keywords = new() { "valentine", "valentines", "sweetheart", "cupid" } },
        new() { Id = "easter", Name = "Easter", DateKind = "easter", ObservanceMonth = 1, ObservanceDay = 1, DaysBefore = 30, DaysAfter = 0, Logo = "logos/easter.png", Keywords = new() { "easter", "bunny", "rabbit", "egg hunt", "resurrection", "paschal" } },
        new() { Id = "independence", Name = "Independence Day", DateKind = "fixed", ObservanceMonth = 7, ObservanceDay = 4, DaysBefore = 9, DaysAfter = 0, Logo = "logos/independence.png", Keywords = new() { "independence day", "july 4", "july fourth", "fourth of july", "4th of july", "fireworks", "patriotic" } },
        new() { Id = "halloween", Name = "Halloween", DateKind = "fixed", ObservanceMonth = 10, ObservanceDay = 31, DaysBefore = 30, DaysAfter = 0, BleedIntoPriorMonth = true, Logo = "logos/halloween.png", EarlyLogo = "logos/October.png", EarlyLogoUntilDay = 15, Keywords = new() { "halloween", "spooky", "ghost", "witch", "pumpkin", "haunted", "october", "trick or treat", "horror" } },
        new() { Id = "thanksgiving", Name = "Thanksgiving", DateKind = "thanksgiving", ObservanceMonth = 1, ObservanceDay = 1, DaysBefore = 30, DaysAfter = 0, Logo = "logos/thanksgiving.png", Keywords = new() { "thanksgiving", "turkey", "pilgrim", "harvest feast" } },
        new() { Id = "christmas", Name = "Christmas", DateKind = "fixed", ObservanceMonth = 12, ObservanceDay = 25, DaysBefore = 24, DaysAfter = 0, Logo = "logos/christmas.png", Keywords = new() { "christmas", "xmas", "santa", "nutcracker", "grinch", "snowman", "krampus", "noel", "nativity" } },
    };

    public static DateTime EasterSunday(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        int m = (a + (11 * h) + (22 * l)) / 451;
        int month = (h + l - (7 * m) + 114) / 31;
        int day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateTime(year, month, day);
    }

    public static DateTime UsThanksgiving(int year)
    {
        var date = new DateTime(year, 11, 1);
        while (date.DayOfWeek != DayOfWeek.Thursday) date = date.AddDays(1);
        return date.AddDays(21);
    }

    public static DateTime ObservanceDate(Holiday h, int year)
    {
        return h.DateKind switch
        {
            "fixed" => new DateTime(year, h.ObservanceMonth, h.ObservanceDay),
            "easter" => EasterSunday(year),
            "thanksgiving" => UsThanksgiving(year),
            _ => new DateTime(year, h.ObservanceMonth, h.ObservanceDay)
        };
    }

    public static (DateTime start, DateTime end) Window(Holiday h, DateTime observance)
    {
        var start = observance.AddDays(-Math.Min(h.DaysBefore, 30));
        var end = observance.AddDays(h.DaysAfter);
        if (h.BleedIntoPriorMonth)
        {
            var priorMonthStart = new DateTime(observance.Year, observance.Month, 1).AddMonths(-1);
            if (start > priorMonthStart) start = priorMonthStart;
        }
        return (start, end);
    }

    public static Holiday? ActiveHoliday(DateTime date)
    {
        Holiday? best = null;
        int bestDist = int.MaxValue;
        foreach (var h in Holidays)
        {
            foreach (var year in new[] { date.Year - 1, date.Year, date.Year + 1 })
            {
                var obs = ObservanceDate(h, year);
                var (start, end) = Window(h, obs);
                if (date < start || date > end) continue;
                var dist = Math.Abs((date - obs).Days);
                if (dist < bestDist) { best = h; bestDist = dist; }
            }
        }
        return best;
    }

    public static Holiday? ActiveOrUpcoming(DateTime date, int maxDaysBefore)
    {
        if (ActiveHoliday(date) is { } active) return active;
        Holiday? best = null;
        int bestDist = int.MaxValue;
        foreach (var h in Holidays)
        {
            foreach (var year in new[] { date.Year, date.Year + 1 })
            {
                var obs = ObservanceDate(h, year);
                if (obs < date) continue;
                var dist = (obs - date).Days;
                if (dist < 0 || dist > maxDaysBefore) continue;
                if (dist < bestDist) { best = h; bestDist = dist; }
            }
        }
        return best;
    }

    public static string LogoFor(Holiday? holiday, DateTime date)
    {
        if (holiday is null) return "logos/off-season.png";
        if (holiday.EarlyLogo is not null && holiday.EarlyLogoUntilDay is not null
            && date.Month == ObservanceDate(holiday, date.Year).Month
            && date.Day <= holiday.EarlyLogoUntilDay.Value)
            return holiday.EarlyLogo!;
        return holiday.Logo;
    }

    public static bool Matches(Dictionary<string, object> item, Holiday holiday)
    {
        var blob = "";
        if (item.TryGetValue("title", out var t)) blob += " " + t;
        if (item.TryGetValue("overview", out var o)) blob += " " + o;
        if (item.TryGetValue("tags", out var tags) && tags is List<object> tagList)
            foreach (var tag in tagList) blob += " " + tag;
        if (item.TryGetValue("genres", out var genres) && genres is List<object> genreList)
            foreach (var g in genreList) blob += " " + g;
        var lower = blob.ToLowerInvariant();
        return holiday.Keywords.Any(k => lower.Contains(k.ToLowerInvariant()));
    }
}

public class HolidayChannelPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.holidaychannel", "The Holiday Channel", "3.0.0", "3.0.0", Category: "catalog",
        Permissions: new List<string> { "storage:read", "storage:write", "network:outbound", "api:routes", "ui:pages" });

    private PluginApi? _api;
    public Task OnLoadAsync(PluginApi api) { _api = api; return Task.CompletedTask; }
    public Task OnEnableAsync() => Task.CompletedTask;
    public Task OnDisableAsync() => Task.CompletedTask;
    public void OnUnload() { }
    public Task OnConfigAsync(JsonElement config) => Task.CompletedTask;

    public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/calendar", (string? date) =>
        {
            DateTime d;
            if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var parsed)) d = parsed;
            else d = DateTime.Today;

            var active = HolidayCalendar.ActiveHoliday(d);
            var upcoming = HolidayCalendar.ActiveOrUpcoming(d, 30);
            var holidays = HolidayCalendar.Holidays.Select(h => new
            {
                id = h.Id,
                name = h.Name,
                logo = h.Logo,
                keywords = h.Keywords,
            }).ToList();

            return Results.Ok(new
            {
                date = d.ToString("yyyy-MM-dd"),
                active = active is null ? null : new { id = active.Id, name = active.Name, logo = active.Logo, observed = HolidayCalendar.ObservanceDate(active, d.Year).ToString("yyyy-MM-dd") },
                upcoming = upcoming is null ? null : new { id = upcoming.Id, name = upcoming.Name, logo = upcoming.Logo },
                logo = HolidayCalendar.LogoFor(active, d),
                holidays
            });
        });

        endpoints.MapPost("/lineup", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            var dateStr = doc.RootElement.TryGetProperty("date", out var d) ? d.GetString() : null;
            DateTime date;
            if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var parsed)) date = parsed;
            else date = DateTime.Today;

            var items = new List<Dictionary<string, object>>();
            if (doc.RootElement.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsEl.EnumerateArray())
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in item.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String) dict[prop.Name] = prop.Value.GetString()!;
                        else if (prop.Value.ValueKind == JsonValueKind.Number) dict[prop.Name] = prop.Value.GetInt32();
                        else if (prop.Value.ValueKind == JsonValueKind.Array) dict[prop.Name] = prop.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                    }
                    items.Add(dict);
                }
            }

            var holiday = HolidayCalendar.ActiveHoliday(date);
            if (holiday is null)
            {
                return Results.Ok(new
                {
                    date = date.ToString("yyyy-MM-dd"),
                    holiday = (object?)null,
                    logo = "logos/off-season.png",
                    matches = Array.Empty<object>(),
                    off_season = true,
                    off_season_media = "The Holiday Channel.mkv"
                });
            }

            var observance = HolidayCalendar.ObservanceDate(holiday, date.Year);
            var (start, end) = HolidayCalendar.Window(holiday, observance);
            var matches = items.Where(item => HolidayCalendar.Matches(item, holiday)).Select(item => new
            {
                match_key = item.TryGetValue("match_key", out var mk) ? mk : (item.TryGetValue("matchKey", out var mk2) ? mk2 : null),
                kind = item.TryGetValue("kind", out var k) ? k : null,
                title = item.TryGetValue("title", out var t) ? t : null,
                year = item.TryGetValue("year", out var y) ? y : null,
            }).ToList();

            return Results.Ok(new
            {
                date = date.ToString("yyyy-MM-dd"),
                holiday = new { id = holiday.Id, name = holiday.Name, logo = holiday.Logo, observed = observance.ToString("yyyy-MM-dd"), window_start = start.ToString("yyyy-MM-dd"), window_end = end.ToString("yyyy-MM-dd") },
                logo = HolidayCalendar.LogoFor(holiday, date),
                matches,
                off_season = false
            });
        });
    }

    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => null;
}
