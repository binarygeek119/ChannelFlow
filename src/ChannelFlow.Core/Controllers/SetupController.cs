using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

public record DbTestRequest(string Provider, string ConnectionString);

[ApiController]
[Route("api/setup")]
public class SetupController : ControllerBase
{
    [HttpPost("test-db")]
    public async Task<IActionResult> TestDb([FromBody] DbTestRequest req)
    {
        try
        {
            var opts = new DbContextOptionsBuilder<ChannelFlowDbContext>();
            switch (req.Provider.ToLowerInvariant())
            {
                case "postgres":
                case "postgresql":
                    opts.UseNpgsql(req.ConnectionString);
                    break;
                case "mariadb":
                case "mysql":
                    opts.UseMySql(req.ConnectionString, ServerVersion.AutoDetect(req.ConnectionString));
                    break;
                default:
                    opts.UseSqlite(req.ConnectionString);
                    break;
            }
            using var db = new ChannelFlowDbContext(opts.Options);
            await db.Database.EnsureCreatedAsync();
            return Ok("connection successful");
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }
}
