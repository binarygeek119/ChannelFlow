using Microsoft.AspNetCore.Mvc;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/lineups")]
public class LineupsController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    public LineupsController(IWebHostEnvironment env) => _env = env;

    [HttpGet("{channelId}")]
    public IActionResult Get(string channelId)
    {
        channelId = channelId.ToLowerInvariant();
        var path = Path.Combine(_env.ContentRootPath, "data", "lineups", channelId + ".json");
        if (!System.IO.File.Exists(path)) return NotFound();
        return Content(System.IO.File.ReadAllText(path), "application/json");
    }
}
