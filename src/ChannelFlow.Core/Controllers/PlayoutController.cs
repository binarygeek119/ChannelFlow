using ChannelFlow.PluginSdk;
using Microsoft.AspNetCore.Mvc;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/playout")]
public class PlayoutController : ControllerBase
{
    private readonly ICoreData _core;
    private readonly IWebHostEnvironment _env;
    public PlayoutController(ICoreData core, IWebHostEnvironment env) { _core = core; _env = env; }

    [HttpPost("generate/{channelId}")]
    public async Task<IActionResult> Generate(string channelId)
    {
        channelId = channelId.ToLowerInvariant();
        await _core.GeneratePlayoutAsync(channelId);
        return Ok(new { ok = true, channelId });
    }

    [HttpPost("generate-all")]
    public async Task<IActionResult> GenerateAll()
    {
        var channels = await _core.ChannelsAsync();
        foreach (var ch in channels) await _core.GeneratePlayoutAsync(ch.Id);
        return Ok(new { ok = true, count = channels.Count });
    }

    [HttpGet("{channelId}")]
    public IActionResult Get(string channelId)
    {
        channelId = channelId.ToLowerInvariant();
        var path = Path.Combine(_env.ContentRootPath, "data", "playout", channelId + ".json");
        if (!System.IO.File.Exists(path)) return NotFound();
        return Content(System.IO.File.ReadAllText(path), "application/json");
    }
}
