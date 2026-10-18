using System.Security.Cryptography;
using ChannelFlow.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/quickpin")]
public class QuickPinController : ControllerBase
{
    private readonly ChannelFlowDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    public QuickPinController(ChannelFlowDbContext db, IHttpClientFactory httpFactory)
    {
        _db = db;
        _httpFactory = httpFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var settings = await _db.GeneralSettings.FindAsync("general");
        if (settings is null) { settings = new GeneralSettings(); _db.GeneralSettings.Add(settings); await _db.SaveChangesAsync(); }
        var payload = QuickPinService.BuildPayload(settings, true, "");
        var server = QuickPinService.ResolvePinServer();
        return Ok(new { payload, server, relay = server });
    }

    [HttpPost("pair")]
    public async Task<IActionResult> Pair([FromBody] QuickPinPairRequest input)
    {
        if (!QuickPinService.IsValidPin(input.Pin))
            return BadRequest(new { error = "enter the 8-character PIN shown on the app" });

        var settings = await _db.GeneralSettings.FindAsync("general");
        if (settings is null) { settings = new GeneralSettings(); _db.GeneralSettings.Add(settings); await _db.SaveChangesAsync(); }

        if (string.IsNullOrWhiteSpace(settings.LocalUrl) && string.IsNullOrWhiteSpace(settings.PublicUrl))
            return BadRequest(new { error = "set a local or public URL on General Settings before pairing" });

        var host = Request.Headers.Host.ToString();
        var primaryLocal = QuickPinService.PrimaryIsLocal(settings, host);

        // Generate a random API key for the paired app
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var payload = QuickPinService.BuildPayload(settings, primaryLocal, apiKey);
        var plaintext = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
        var ciphertext = QuickPinService.Encrypt(input.Pin, plaintext);

        var server = QuickPinService.ResolvePinServer();
        using var http = _httpFactory.CreateClient();
        bool delivered;
        try
        {
            delivered = await QuickPinService.DeliverAsync(http, server, input.Pin, ciphertext);
        }
        catch (Exception e)
        {
            return BadRequest(new { error = e.Message });
        }

        return Ok(new { delivered, server });
    }
}

public record QuickPinPairRequest(string Pin);
