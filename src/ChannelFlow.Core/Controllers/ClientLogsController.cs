using Microsoft.AspNetCore.Mvc;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/client-logs")]
public class ClientLogsController : ControllerBase
{
    private readonly ClientLogsService _svc;
    public ClientLogsController(ClientLogsService svc) => _svc = svc;

    [HttpGet]
    public IActionResult ListDevices() => Ok(new { devices = _svc.ListDevices() });

    [HttpGet("{deviceId}")]
    public IActionResult GetDevice(string deviceId, [FromQuery] string? file)
    {
        if (string.IsNullOrEmpty(file))
        {
            var files = _svc.ListFiles(deviceId);
            return Ok(new { device = deviceId, files });
        }
        var content = _svc.ReadFile(deviceId, file);
        if (content is null) return NotFound();
        return Content(content, "text/plain");
    }

    [HttpPost]
    public IActionResult Ingest([FromBody] ClientLogIngestRequest req)
    {
        if (req.Entries.Count == 0) return BadRequest("at least one log entry is required");
        _svc.Ingest(req.DeviceId, req.DeviceName, req.AppVersion, req.OsVersion, req.Entries);
        return Ok(new { accepted = req.Entries.Count, device_id = req.DeviceId });
    }
}
