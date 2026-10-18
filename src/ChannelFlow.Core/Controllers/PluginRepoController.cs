using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/plugin-repos")]
public class PluginRepoController : ControllerBase
{
    private readonly ChannelFlowDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    public PluginRepoController(ChannelFlowDbContext db, IHttpClientFactory httpFactory)
    {
        _db = db;
        _httpFactory = httpFactory;
    }

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await _db.PluginRepos.ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] PluginRepo repo)
    {
        if (string.IsNullOrWhiteSpace(repo.Url)) return BadRequest("url required");
        if (string.IsNullOrWhiteSpace(repo.Name)) repo.Name = repo.Url;
        _db.PluginRepos.Add(repo);
        await _db.SaveChangesAsync();
        return Ok(repo);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id)
    {
        var r = await _db.PluginRepos.FindAsync(id);
        if (r is null) return NotFound();
        _db.PluginRepos.Remove(r);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("{id:int}/index")]
    public async Task<IActionResult> FetchIndex(int id)
    {
        var repo = await _db.PluginRepos.FindAsync(id);
        if (repo is null) return NotFound();
        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var indexUrl = repo.Url.TrimEnd('/') + "/plugins.json";
            var res = await http.GetAsync(indexUrl);
            if (!res.IsSuccessStatusCode) return BadRequest($"HTTP {(int)res.StatusCode} from {indexUrl}");
            var json = await res.Content.ReadAsStringAsync();
            return Content(json, "application/json");
        }
        catch (Exception e)
        {
            return BadRequest(new { error = e.Message });
        }
    }
}
