using Microsoft.AspNetCore.Mvc;

namespace ChannelFlow.Core.Controllers;

public record MigrateSqliteRequest(string V2DbPath);
public record MigratePostgresRequest(string ConnectionString);

[ApiController]
[Route("api/migration")]
public class MigrationController : ControllerBase
{
    private readonly MigrationService _svc;
    public MigrationController(MigrationService svc) => _svc = svc;

    [HttpPost("sqlite")]
    public async Task<IActionResult> FromSqlite([FromBody] MigrateSqliteRequest req)
    {
        var report = await _svc.MigrateFromSqliteAsync(req.V2DbPath);
        return Ok(report);
    }

    [HttpPost("fix-schema")]
    public async Task<IActionResult> FixSchema()
    {
        var fixes = await _svc.FixSchemaAsync();
        return Ok(fixes);
    }

    [HttpPost("postgres")]
    public async Task<IActionResult> FromPostgres([FromBody] MigratePostgresRequest req)
    {
        var report = await _svc.MigrateFromPostgresAsync(req.ConnectionString);
        return Ok(report);
    }
}
