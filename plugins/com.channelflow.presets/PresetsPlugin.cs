using System.Text.Json;
using ChannelFlow.PluginSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ChannelFlow.Plugin.Presets;

public class Preset
{
    public string Id { get; set; } = "";
    public double Legacy { get; set; }
    public double Subchannel { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string Logo { get; set; } = "";
}

public class PresetsPlugin : IPlugin
{
    public PluginManifest Metadata => new("com.channelflow.presets", "Presets", "3.0.0", "3.0.0", Category: "catalog",
        Permissions: new List<string> { "storage:read", "storage:write", "network:outbound", "api:routes", "ui:pages" });

    private PluginApi? _api;
    private static readonly List<Preset> Presets = new()
    {
        new() { Id = "channelflow-flashback", Legacy = 119.0, Subchannel = 119.1, Name = "FlashBack TV", Category = "TV Shows", Description = "1970–2009 TV and movies (first-episode year for series)", Logo = "logos/Shows/FlashBack_TV.png" },
        new() { Id = "channelflow-retro", Legacy = 120.0, Subchannel = 119.2, Name = "Retro TV", Category = "TV Shows", Description = "1910–1969 TV and movies (first-episode year for series)", Logo = "logos/Shows/Retro_TV.png" },
        new() { Id = "channelflow-open-swim", Legacy = 121.0, Subchannel = 119.3, Name = "[OpenSwim]", Category = "TV Shows", Description = "Nick, Disney, Fox Kids, and Cartoon Network style kids TV/movies; any year; TV-PG max", Logo = "logos/Shows/[open_swim].png" },
        new() { Id = "channelflow-reality", Legacy = 135.0, Subchannel = 124.1, Name = "Flip Television", Category = "TV Shows", Description = "Reality TV themed shows and movies", Logo = "logos/Shows/Flip_Television.png" },
        new() { Id = "channelflow-past-tense-news", Legacy = 137.0, Subchannel = 124.3, Name = "Past Tense News", Category = "News", Description = "Home movies treated as live breaking news", Logo = "logos/News/Past_Tense_News.png" },
        new() { Id = "channelflow-crime", Legacy = 136.0, Subchannel = 124.2, Name = "Cops And Robbers", Category = "TV Shows", Description = "Crime and cop themed TV shows and movies (genre or plot)", Logo = "logos/Shows/cops_and_robbers.png" },
        new() { Id = "channelflow-comedy", Legacy = 138.0, Subchannel = 124.4, Name = "Slappy", Category = "TV Shows", Description = "Fox network clone: comedy TV and movies with Friday 5–8pm Slappy's Toon Takeover", Logo = "logos/Shows/Slappy.png" },
        new() { Id = "channelflow-game-shows", Legacy = 146.0, Subchannel = 126.1, Name = "Winning", Category = "TV Shows", Description = "Game shows channel", Logo = "logos/Shows/winning.png" },
        new() { Id = "channelflow-education", Legacy = 147.0, Subchannel = 126.2, Name = "GET LEARNEDED", Category = "TV Shows", Description = "Educational TV shows and movies", Logo = "logos/Shows/GET_LEARNEDED.png" },
        new() { Id = "channelflow-youtube", Legacy = 148.0, Subchannel = 126.3, Name = "YouTube TV", Category = "TV Shows", Description = "Content from the Jellyfin TV library curated for YouTube", Logo = "logos/Shows/YouTube_TV.png" },
        new() { Id = "channelflow-creature", Legacy = 203.0, Subchannel = 203.1, Name = "Creature Double Feature", Category = "Movies", Description = "Creature and monster movies and TV (genre, plot, or tags)", Logo = "logos/Movies/Creature_Double_Feature.png" },
        new() { Id = "channelflow-hero", Legacy = 204.0, Subchannel = 203.2, Name = "Hero TV", Category = "Movies", Description = "Anyone who saves or protects people — heroes, rescuers, and champions", Logo = "logos/Movies/Hero_TV.png" },
        new() { Id = "channelflow-funny", Legacy = 205.0, Subchannel = 203.3, Name = "That's Funny", Category = "Movies", Description = "Stand-up comedy movies and shows", Logo = "logos/Movies/That's_Funny.png" },
        new() { Id = "channelflow-parody", Legacy = 312.0, Subchannel = 312.1, Name = "The Parody Channel", Category = "Music Videos", Description = "Parody music videos", Logo = "logos/Music Videos Channels/The-Parody-Channel.png" },
        new() { Id = "channelflow-rap", Legacy = 313.0, Subchannel = 312.2, Name = "Rap On Tap", Category = "Music Videos", Description = "Rap and hip hop music videos", Logo = "logos/Music Videos Channels/Rap-On-Tap.png" },
        new() { Id = "channelflow-music-video", Legacy = 314.0, Subchannel = 312.3, Name = "HeadPhone Jack", Category = "Music Videos", Description = "All other music videos", Logo = "logos/Music Videos Channels/HeadPhone_Jack.png" },
    };

    public Task OnLoadAsync(PluginApi api) { _api = api; return Task.CompletedTask; }
    public Task OnEnableAsync() => Task.CompletedTask;
    public Task OnDisableAsync() => Task.CompletedTask;
    public void OnUnload() { }
    public Task OnConfigAsync(JsonElement config) => Task.CompletedTask;

    public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/presets", (string? mode, string? weatherstar) =>
        {
            var m = mode ?? "retro";
            var list = Presets.Select(p => new
            {
                id = p.Id,
                number = FormatNumber(m == "modern" ? p.Subchannel : p.Legacy),
                retro = FormatNumber(p.Legacy),
                modern = FormatNumber(p.Subchannel),
                name = p.Name,
                category = p.Category,
                description = p.Description,
                logo = p.Logo,
                exists = false // would need channel lookup; simplified
            }).ToList();
            return Results.Ok(new { mode = m, presets = list, weatherstar = (object?)null, note = "Presets are a quick start — channels can also be added on the Channels tab." });
        });
    }

    private static string FormatNumber(double n) => n % 1 == 0 ? $"{(int)n}" : $"{n}";

    public IEnumerable<UiContribution> UiContributions => Array.Empty<UiContribution>();
    public PluginHealth Health => new(true, "ok");
    public IMediaSource? MediaSource => null;
}
