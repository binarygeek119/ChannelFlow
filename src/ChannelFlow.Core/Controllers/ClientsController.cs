using System.Security.Cryptography;
using System.Text.Json;
using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/clients")]
public class ClientsController : ControllerBase
{
    private readonly ChannelFlowDbContext _db;
    public ClientsController(ChannelFlowDbContext db) => _db = db;

    private const string Ns = "com.channelflow.core";
    private const string ServerKeyKey = "server_api_key";
    private const string ClientsKey = "paired_clients";

    private async Task<string> GetServerApiKeyAsync()
    {
        var s = await _db.Settings.FindAsync(ServerKeyKey);
        if (s is not null && !string.IsNullOrEmpty(s.Value)) return s.Value;
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        if (s is null) _db.Settings.Add(new AppSetting { Key = ServerKeyKey, Value = key });
        else s.Value = key;
        await _db.SaveChangesAsync();
        return key;
    }

    private async Task<List<JsonElement>> GetClientsAsync()
    {
        var s = await _db.Settings.FindAsync(ClientsKey);
        if (s is null || string.IsNullOrEmpty(s.Value)) return new();
        try
        {
            using var doc = JsonDocument.Parse(s.Value);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new();
        }
        catch { return new(); }
    }

    private async Task SaveClientsAsync(List<object> clients)
    {
        var json = JsonSerializer.Serialize(clients);
        var s = await _db.Settings.FindAsync(ClientsKey);
        if (s is null) _db.Settings.Add(new AppSetting { Key = ClientsKey, Value = json });
        else s.Value = json;
        await _db.SaveChangesAsync();
    }

    private static string KeyHint(string key)
    {
        if (key.Length <= 10) return "••••";
        return $"{key[..6]}…{key[^4..]}";
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var serverKey = await GetServerApiKeyAsync();
        var clients = await GetClientsAsync();
        var list = clients.Select(c =>
        {
            var id = c.TryGetProperty("id", out var i) ? i.GetString() : "";
            var key = c.TryGetProperty("api_key", out var k) ? k.GetString() ?? "" : "";
            var label = c.TryGetProperty("label", out var l) ? l.GetString() : "";
            var created = c.TryGetProperty("created_at", out var ca) ? ca.GetString() : "";
            var lastSeen = c.TryGetProperty("last_seen_at", out var ls) && ls.ValueKind != JsonValueKind.Null ? ls.GetString() : null;
            return new { id, key_hint = KeyHint(key), label, created_at = created, last_seen_at = lastSeen };
        }).ToList();
        return Ok(new { clients = list, server_key_hint = KeyHint(serverKey) });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id)
    {
        var clients = await GetClientsAsync();
        var before = clients.Count;
        var filtered = clients.Where(c => !(c.TryGetProperty("id", out var i) && i.GetString() == id)).ToList();
        if (filtered.Count == before) return NotFound();
        await SaveClientsAsync(filtered.Cast<object>().ToList());
        return Ok(new { removed = true });
    }
}
