using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/guide")]
public class GuideController : ControllerBase
{
    private readonly ChannelFlowDbContext _db;
    public GuideController(ChannelFlowDbContext db) => _db = db;

    [HttpGet("{channelId}")]
    public async Task<IActionResult> Get(string channelId, [FromQuery] string? from, [FromQuery] string? to)
    {
        var cid = channelId.ToUpperInvariant();
        var q = _db.GuidePrograms.AsNoTracking().Where(g => g.ChannelId == cid);
        if (!string.IsNullOrEmpty(from) && DateTimeOffset.TryParse(from, out var f))
            q = q.Where(g => string.Compare(g.Start, f.ToString("o")) >= 0);
        if (!string.IsNullOrEmpty(to) && DateTimeOffset.TryParse(to, out var t))
            q = q.Where(g => string.Compare(g.Start, t.ToString("o")) <= 0);
        return Ok(await q.OrderBy(g => g.Start).ToListAsync());
    }
}
