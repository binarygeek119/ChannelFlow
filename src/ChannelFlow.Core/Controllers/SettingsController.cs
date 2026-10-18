using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly ChannelFlowDbContext _db;
    public SettingsController(ChannelFlowDbContext db) => _db = db;

    [HttpGet("llm")]
    public async Task<IActionResult> GetLlm()
    {
        var s = await _db.Settings.FindAsync("llm");
        if (s is null) return Ok(new LlmConfig());
        try { return Ok(JsonSerializer.Deserialize<LlmConfig>(s.Value) ?? new LlmConfig()); }
        catch { return Ok(new LlmConfig()); }
    }

    [HttpPost("llm")]
    public async Task<IActionResult> SetLlm([FromBody] LlmConfig cfg)
    {
        var s = await _db.Settings.FindAsync("llm");
        var json = JsonSerializer.Serialize(cfg);
        if (s is null) _db.Settings.Add(new AppSetting { Key = "llm", Value = json });
        else s.Value = json;
        await _db.SaveChangesAsync();
        return Ok(cfg);
    }
}
