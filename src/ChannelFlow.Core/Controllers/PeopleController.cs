using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/people")]
public class PeopleController : ControllerBase
{
    private readonly ChannelFlowDbContext _db;
    private readonly IWebHostEnvironment _env;
    public PeopleController(ChannelFlowDbContext db, IWebHostEnvironment env) { _db = db; _env = env; }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] string? role)
    {
        var q = _db.People.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(p => p.Name.Contains(search));
        if (!string.IsNullOrWhiteSpace(role))
        {
            var personIds = await _db.PersonCredits
                .AsNoTracking()
                .Where(c => c.Role.ToLower() == role.ToLower())
                .Select(c => c.PersonId)
                .Distinct()
                .ToListAsync();
            q = q.Where(p => personIds.Contains(p.Id));
        }
        return Ok(await q.OrderBy(p => p.Name).ToListAsync());
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var p = await _db.People.FindAsync(id);
        if (p is null) return NotFound();
        var credits = await _db.PersonCredits.Where(c => c.PersonId == id).ToListAsync();
        return Ok(new { person = p, credits });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Person person)
    {
        _db.People.Add(person);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = person.Id }, person);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Person person)
    {
        var existing = await _db.People.FindAsync(id);
        if (existing is null) return NotFound();
        existing.Name = person.Name;
        existing.ImagePath = person.ImagePath;
        await _db.SaveChangesAsync();
        return Ok(existing);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var p = await _db.People.FindAsync(id);
        if (p is null) return NotFound();
        _db.People.Remove(p);
        _db.PersonCredits.RemoveRange(_db.PersonCredits.Where(c => c.PersonId == id));
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/credits")]
    public async Task<IActionResult> AddCredit(Guid id, [FromBody] PersonCredit credit)
    {
        credit.PersonId = id;
        _db.PersonCredits.Add(credit);
        await _db.SaveChangesAsync();
        return Ok(credit);
    }
    [HttpPost("{id:guid}/image")]
    public async Task<IActionResult> UploadImage(Guid id, IFormFile file)
    {
        var person = await _db.People.FindAsync(id);
        if (person is null) return NotFound();
        if (file is null || file.Length == 0) return BadRequest("no file");
        var ext = Path.GetExtension(file.FileName);
        var fileName = $"{id}{ext}";
        var dir = Path.Combine(_env.ContentRootPath, "data", "images");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        using (var stream = System.IO.File.Create(path))
            await file.CopyToAsync(stream);
        person.ImagePath = $"/images/{fileName}";
        await _db.SaveChangesAsync();
        return Ok(new { imagePath = person.ImagePath });
    }
}
