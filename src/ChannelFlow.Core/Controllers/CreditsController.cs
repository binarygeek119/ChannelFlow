using ChannelFlow.Database;
using ChannelFlow.PluginSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/credits")]
public class CreditsController : ControllerBase
{
    private readonly ICoreData _core;
    private readonly ChannelFlowDbContext _db;
    public CreditsController(ICoreData core, ChannelFlowDbContext db) { _core = core; _db = db; }

    [HttpGet("media/{*matchKey}")]
    public async Task<IActionResult> ForMedia(string matchKey)
    {
        var credits = await _core.GetCreditsForMediaAsync(matchKey);
        return Ok(credits);
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] PersonCreditDto credit)
    {
        var c = await _core.AddCreditAsync(credit);
        return Ok(c);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id)
    {
        await _core.RemoveCreditAsync(id);
        return NoContent();
    }

    [HttpGet("person/{personId:guid}")]
    public async Task<IActionResult> ForPerson(Guid personId)
    {
        var credits = await _db.PersonCredits.Where(c => c.PersonId == personId).ToListAsync();
        return Ok(credits);
    }
}
