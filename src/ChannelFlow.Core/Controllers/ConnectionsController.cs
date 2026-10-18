using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/connections")]
public class ConnectionsController : ControllerBase
{
    private readonly ChannelFlowDbContext _db;
    public ConnectionsController(ChannelFlowDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await _db.Connections.ToListAsync());

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var c = await _db.Connections.FindAsync(id);
        return c is null ? NotFound() : Ok(c);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Connection conn)
    {
        _db.Connections.Add(conn);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = conn.Id }, conn);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Connection conn)
    {
        var existing = await _db.Connections.FindAsync(id);
        if (existing is null) return NotFound();
        existing.Kind = conn.Kind;
        existing.ConfigJson = conn.ConfigJson;
        await _db.SaveChangesAsync();
        return Ok(existing);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.Connections.FindAsync(id);
        if (c is null) return NotFound();
        _db.Connections.Remove(c);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
