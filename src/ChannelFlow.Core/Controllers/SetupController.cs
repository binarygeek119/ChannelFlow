using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

public record DbTestRequest(string Provider, string ConnectionString);
public record DbStatusResponse(string Provider, string ConnectionString, bool Connected, int TableCount);

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


    [HttpGet("db-status")]
    public async Task<IActionResult> DbStatus()
    {
        // Read from config file or env
        var setupConfigPath = "/config/data/db-config.json";
        if (!System.IO.File.Exists(setupConfigPath))
            setupConfigPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "db-config.json");
        string provider = "sqlite";
        string connStr = "Data Source=channelflow.db";
        if (System.IO.File.Exists(setupConfigPath))
        {
            try
            {
                var json = System.IO.File.ReadAllText(setupConfigPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("provider", out var p)) provider = p.GetString() ?? provider;
                if (doc.RootElement.TryGetProperty("connectionString", out var c)) connStr = c.GetString() ?? connStr;
            }
            catch { }
        }
        // Also check env vars
        var envProvider = Environment.GetEnvironmentVariable("DATABASE_PROVIDER");
        var envConnStr = Environment.GetEnvironmentVariable("CONNECTION_STRING");
        if (!string.IsNullOrEmpty(envProvider)) provider = envProvider;
        if (!string.IsNullOrEmpty(envConnStr)) connStr = envConnStr;

        bool connected = false;
        int tableCount = 0;
        try
        {
            var opts = new DbContextOptionsBuilder<ChannelFlowDbContext>();
            switch (provider.ToLowerInvariant())
            {
                case "postgres":
                case "postgresql":
                    opts.UseNpgsql(connStr);
                    break;
                case "mariadb":
                case "mysql":
                    opts.UseMySql(connStr, ServerVersion.AutoDetect(connStr));
                    break;
                default:
                    opts.UseSqlite(connStr);
                    break;
            }
            using var db = new ChannelFlowDbContext(opts.Options);
            connected = await db.Database.CanConnectAsync();
            if (connected)
            {
                // Count tables via raw query
                try
                {
                    var conn = db.Database.GetDbConnection();
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    if (provider.ToLowerInvariant().Contains("postgres"))
                        cmd.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema='public'";
                    else if (provider.ToLowerInvariant().Contains("maria") || provider.ToLowerInvariant().Contains("mysql"))
                        cmd.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema=DATABASE()";
                    else
                        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table'";
                    var result = await cmd.ExecuteScalarAsync();
                    tableCount = Convert.ToInt32(result);
                    await conn.CloseAsync();
                }
                catch { }
            }
        }
        catch { connected = false; }

        return Ok(new DbStatusResponse(provider, connStr, connected, tableCount));
    }

    [HttpPost("configure")]
    public IActionResult Configure([FromBody] DbTestRequest req)
    {
        try
        {
            // Validate first
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
            db.Database.EnsureCreated();

            // Save config
            var configDir = "/config/data";
            if (!Directory.Exists(configDir)) configDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
            Directory.CreateDirectory(configDir);
            var configPath = Path.Combine(configDir, "db-config.json");
            var json = System.Text.Json.JsonSerializer.Serialize(new { provider = req.Provider, connectionString = req.ConnectionString });
            System.IO.File.WriteAllText(configPath, json);
            return Ok("configured - restart the application to apply");
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }
}
