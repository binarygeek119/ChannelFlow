using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/plugin-repos")]
public class PluginRepoController : ControllerBase
{
    private readonly ChannelFlowDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly PluginManager _pm;
    public PluginRepoController(ChannelFlowDbContext db, IHttpClientFactory httpFactory, PluginManager pm)
    {
        _db = db;
        _httpFactory = httpFactory;
        _pm = pm;
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

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog()
    {
        var repos = await _db.PluginRepos.Where(r => r.Enabled).ToListAsync();
        var installedIds = _pm.Plugins.Select(p => p.Metadata.Id).ToHashSet();
        var result = new List<object>();
        var errors = new List<string>();

        foreach (var repo in repos)
        {
            try
            {
                using var http = _httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(15);
                var indexUrl = repo.Url.TrimEnd('/') + "/plugins.json";
                var res = await http.GetAsync(indexUrl);
                if (!res.IsSuccessStatusCode) { errors.Add(repo.Name); continue; }
                var json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var plugins = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : (doc.RootElement.TryGetProperty("plugins", out var p) ? p : default);
                if (plugins.ValueKind != JsonValueKind.Array) continue;
                foreach (var pl in plugins.EnumerateArray())
                {
                    var id = pl.TryGetProperty("id", out var ip) ? ip.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;
                    result.Add(new
                    {
                        id = id,
                        name = pl.TryGetProperty("name", out var np) ? np.GetString() : id,
                        description = pl.TryGetProperty("description", out var dp) ? dp.GetString() : "",
                        category = pl.TryGetProperty("category", out var cp) ? cp.GetString() : "",
                        owner = pl.TryGetProperty("owner", out var op) ? op.GetString() : "",
                        imageUrl = pl.TryGetProperty("imageUrl", out var iup) ? iup.GetString() : null,
                        compatible = true,
                        installed = installedIds.Contains(id),
                        repository = repo.Url,
                        repositoryName = repo.Name
                    });
                }
            }
            catch (Exception e)
            {
                errors.Add($"{repo.Name}: {e.Message}");
            }
        }

        return Ok(new { plugins = result, errors = errors });
    }
}
