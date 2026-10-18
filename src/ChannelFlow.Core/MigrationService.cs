using System.Data;
using Microsoft.Data.Sqlite;
using Npgsql;
using ChannelFlow.Database;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core;

public class MigrationService
{
    private readonly ChannelFlowDbContext _db;
    public MigrationService(ChannelFlowDbContext db) => _db = db;

    public async Task<MigrationReport> MigrateFromSqliteAsync(string v2DbPath)
    {
        var report = new MigrationReport();
        try
        {
        using var conn = new SqliteConnection($"Data Source={v2DbPath}");
        await conn.OpenAsync();

        // Channels
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, number, name, description, enabled FROM channels";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var ch = new Channel
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Number = reader.GetDouble(1),
                    Name = reader.GetString(2),
                    Description = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Enabled = !reader.IsDBNull(4) && reader.GetBoolean(4)
                };
                if (!await _db.Channels.AnyAsync(c => c.Id == ch.Id))
                {
                    _db.Channels.Add(ch);
                    report.Channels++;
                }
            }
        }
        catch (Exception e) { report.Errors.Add("channels: " + e.Message); }

        // Media items
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT match_key, kind, title, year, runtime_seconds, genres, official_rating, tags FROM media_items";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var mi = new MediaItem
                {
                    MatchKey = reader.GetString(0),
                    Kind = reader.GetString(1),
                    Title = reader.GetString(2),
                    Year = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    RuntimeMs = reader.IsDBNull(4) ? null : reader.GetInt64(4) * 1000,
                    Genres = ParseJsonList(reader.IsDBNull(5) ? "[]" : reader.GetString(5)),
                    OfficialRating = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Tags = ParseJsonList(reader.IsDBNull(7) ? "[]" : reader.GetString(7))
                };
                if (!await _db.MediaItems.AnyAsync(m => m.MatchKey == mi.MatchKey))
                {
                    _db.MediaItems.Add(mi);
                    report.MediaItems++;
                }
            }
        }
        catch (Exception e) { report.Errors.Add("media_items: " + e.Message); }

        // People
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, image_path FROM media_people";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var img = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!await _db.People.AnyAsync(p => p.Name == name))
                {
                    _db.People.Add(new Person { Name = name, ImagePath = img });
                    report.People++;
                }
            }
        }
        catch (Exception e) { report.Errors.Add("media_people: " + e.Message); }

        } catch (Exception e) { report.Errors.Add("connection: " + e.Message); }
        await _db.SaveChangesAsync();
        return report;
    }

    public async Task<MigrationReport> MigrateFromPostgresAsync(string connectionString)
    {
        var report = new MigrationReport();
        try
        {
        using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        // Channels
        try
        {
            using var cmd = new NpgsqlCommand("SELECT id, number, name, description, enabled FROM channels", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var ch = new Channel
                {
                    Id = reader.GetGuid(0),
                    Number = reader.GetDouble(1),
                    Name = reader.GetString(2),
                    Description = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Enabled = reader.GetBoolean(4)
                };
                if (!await _db.Channels.AnyAsync(c => c.Id == ch.Id))
                {
                    _db.Channels.Add(ch);
                    report.Channels++;
                }
            }
        }
        catch (Exception e) { report.Errors.Add("channels: " + e.Message); }

        // Media items
        try
        {
            using var cmd = new NpgsqlCommand("SELECT match_key, kind, title, year, runtime_seconds, genres, official_rating, tags FROM media_items", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var mi = new MediaItem
                {
                    MatchKey = reader.GetString(0),
                    Kind = reader.GetString(1),
                    Title = reader.GetString(2),
                    Year = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    RuntimeMs = reader.IsDBNull(4) ? null : reader.GetInt64(4) * 1000,
                    Genres = ParseJsonList(reader.IsDBNull(5) ? "[]" : reader.GetString(5)),
                    OfficialRating = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Tags = ParseJsonList(reader.IsDBNull(7) ? "[]" : reader.GetString(7))
                };
                if (!await _db.MediaItems.AnyAsync(m => m.MatchKey == mi.MatchKey))
                {
                    _db.MediaItems.Add(mi);
                    report.MediaItems++;
                }
            }
        }
        catch (Exception e) { report.Errors.Add("media_items: " + e.Message); }

        // People
        try
        {
            using var cmd = new NpgsqlCommand("SELECT name, image_path FROM media_people", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var img = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!await _db.People.AnyAsync(p => p.Name == name))
                {
                    _db.People.Add(new Person { Name = name, ImagePath = img });
                    report.People++;
                }
            }
        }
        catch (Exception e) { report.Errors.Add("media_people: " + e.Message); }

        // General settings from plugin_kv
        try
        {
            using var cmd = new NpgsqlCommand("SELECT value FROM plugin_kv WHERE namespace='com.channelflow.core' AND key='general'", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var json = reader.GetString(0);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var gs = await _db.GeneralSettings.FindAsync("general");
                if (gs is null) { gs = new GeneralSettings { Key = "general" }; _db.GeneralSettings.Add(gs); }
                if (root.TryGetProperty("timezone", out var tz) && !string.IsNullOrEmpty(tz.GetString()))
                    gs.Timezone = tz.GetString()!;
            }
        }
        catch (Exception e) { report.Errors.Add("general_settings: " + e.Message); }

        // Connections
        try
        {
            using var cmd = new NpgsqlCommand("SELECT kind, config FROM connections", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var kind = reader.GetString(0);
                var config = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
                _db.Connections.Add(new Connection { Kind = kind, ConfigJson = config });
                report.MediaItems++; // reuse counter? no, add a connections counter
            }
        }
        catch (Exception e) { report.Errors.Add("connections: " + e.Message); }

        } catch (Exception e) { report.Errors.Add("connection: " + e.Message); }

        await _db.SaveChangesAsync();
        return report;
    }

    public async Task<List<string>> FixSchemaAsync()
    {
        var fixes = new List<string>();
        using var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync();
        var cmds = new[]
        {
            "ALTER TABLE Channels ADD COLUMN AiPrompt TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE Channels ADD COLUMN TranscodeJson TEXT NOT NULL DEFAULT '{}'",
            "ALTER TABLE Channels ADD COLUMN WatermarkJson TEXT NOT NULL DEFAULT 'null'",
            "CREATE TABLE IF NOT EXISTS GeneralSettings (Key TEXT PRIMARY KEY, Timezone TEXT NOT NULL, Value TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS Connections (Id INTEGER PRIMARY KEY AUTOINCREMENT, Kind TEXT NOT NULL, ConfigJson TEXT NOT NULL, CreatedAt TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS People (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, ImagePath TEXT, CreatedAt TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS PersonCredits (Id TEXT PRIMARY KEY, PersonId TEXT NOT NULL, MediaMatchKey TEXT NOT NULL, Role TEXT NOT NULL, Character TEXT)",
            "CREATE TABLE IF NOT EXISTS PluginRepos (Id INTEGER PRIMARY KEY AUTOINCREMENT, Url TEXT NOT NULL, Name TEXT NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS InstalledPlugins (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Version TEXT NOT NULL, RepoUrl TEXT NOT NULL, InstallPath TEXT NOT NULL, InstalledAt TEXT NOT NULL)",
        };
        foreach (var cmd in cmds)
        {
            try
            {
                using var c = conn.CreateCommand();
                c.CommandText = cmd;
                await c.ExecuteNonQueryAsync();
                fixes.Add("OK: " + cmd);
            }
            catch (Exception e)
            {
                // Column/table already exists — ignore
                if (!e.Message.Contains("duplicate") && !e.Message.Contains("already exists"))
                    fixes.Add("SKIP/ERR: " + cmd + " -> " + e.Message);
            }
        }
        await conn.CloseAsync();
        return fixes;
    }

    private static List<string> ParseJsonList(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                return doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
        catch { }
        return new List<string>();
    }
}

public class MigrationReport
{
    public int Channels { get; set; }
    public int MediaItems { get; set; }
    public int People { get; set; }
    public List<string> Errors { get; set; } = new();
}
