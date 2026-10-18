using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/settings/general")]
public class GeneralSettingsController : ControllerBase
{
    private readonly ChannelFlowDbContext _db;
    public GeneralSettingsController(ChannelFlowDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var s = await _db.GeneralSettings.FindAsync("general");
        if (s is null) { s = new GeneralSettings(); _db.GeneralSettings.Add(s); await _db.SaveChangesAsync(); }
        return Ok(s);
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] GeneralSettings settings)
    {
        var s = await _db.GeneralSettings.FindAsync("general");
        if (s is null) { settings.Key = "general"; _db.GeneralSettings.Add(settings); }
        else { s.Timezone = settings.Timezone; s.Value = settings.Value; }
        await _db.SaveChangesAsync();
        return Ok(settings);
    }
}
