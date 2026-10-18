using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/channels")]
public class ChannelsController : ControllerBase
{
    private readonly ChannelFlowDbContext _db;
    public ChannelsController(ChannelFlowDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await _db.Channels.AsNoTracking().OrderBy(c => c.Number).ToListAsync());

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var c = await _db.Channels.FindAsync(id);
        return c is null ? NotFound() : Ok(c);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Channel channel)
    {
        _db.Channels.Add(channel);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = channel.Id }, channel);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Channel channel)
    {
        var existing = await _db.Channels.FindAsync(id);
        if (existing is null) return NotFound();
        existing.Number = channel.Number;
        existing.Name = channel.Name;
        existing.Description = channel.Description;
        existing.Enabled = channel.Enabled;
        existing.AiPrompt = channel.AiPrompt;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(existing);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var c = await _db.Channels.FindAsync(id);
        if (c is null) return NotFound();
        _db.Channels.Remove(c);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
