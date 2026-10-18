using ChannelFlow.PluginSdk;
using Microsoft.AspNetCore.Mvc;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/plugins-manage")]
public class PluginsController : ControllerBase
{
    private readonly PluginManager _pm;
    private readonly IWebHostEnvironment _env;
    public PluginsController(PluginManager pm, IWebHostEnvironment env) { _pm = pm; _env = env; }

    [HttpGet]
    public IActionResult List()
    {
        var list = _pm.Plugins.Select(p => new
        {
            id = p.Metadata.Id,
            name = p.Metadata.Name,
            version = p.Metadata.Version,
            category = p.Metadata.Category,
            author = p.Metadata.Author,
            description = p.Metadata.Description,
            permissions = p.Metadata.Permissions,
            health = p.Health,
            imageUrl = p.Metadata.ImageUrl,
            enabled = p.Health.Ok,
            hasMediaSource = p.MediaSource is not null,
            uiContributions = p.UiContributions
        });
        return Ok(list);
    }

    [HttpPost("{id}/enable")]
    public async Task<IActionResult> Enable(string id)
    {
        await _pm.EnableAsync(id);
        return Ok(new { ok = true });
    }

    [HttpPost("{id}/disable")]
    public async Task<IActionResult> Disable(string id)
    {
        await _pm.DisableAsync(id);
        return Ok(new { ok = true });
    }

    [HttpDelete("{id}")]
    public IActionResult Uninstall(string id)
    {
        _pm.Unload(id);
        return Ok(new { ok = true });
    }
    [HttpPost("install")]
    public async Task<IActionResult> Install([FromBody] InstallRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Url)) return BadRequest("url required");
        try
        {
            var pluginsDir = Path.Combine(_env.ContentRootPath, "plugins");
            Directory.CreateDirectory(pluginsDir);
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(req.Url);
            var tempZip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".zip");
            await System.IO.File.WriteAllBytesAsync(tempZip, bytes);
            var extractDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, extractDir, overwriteFiles: true);

            // find plugin.json
            var manifestPath = Directory.GetFiles(extractDir, "plugin.json", SearchOption.AllDirectories).FirstOrDefault();
            if (manifestPath is null) return BadRequest("no plugin.json found in archive");

            var manifestJson = await System.IO.File.ReadAllTextAsync(manifestPath);
            using var doc = System.Text.Json.JsonDocument.Parse(manifestJson);
            var id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(id)) return BadRequest("plugin.json missing id");

            var pluginDir = Path.Combine(pluginsDir, id);
            if (Directory.Exists(pluginDir)) Directory.Delete(pluginDir, recursive: true);
            Directory.Move(Path.GetDirectoryName(manifestPath)!, pluginDir);

            // load
            var dllName = doc.RootElement.TryGetProperty("entrypoint", out var ep) && ep.TryGetProperty("dll", out var dll) ? dll.GetString() : null;
            if (string.IsNullOrEmpty(dllName) || !System.IO.File.Exists(Path.Combine(pluginDir, dllName)))
                return BadRequest("entrypoint dll not found");

            await _pm.LoadAsync(id, Path.Combine(pluginDir, dllName));
            await _pm.EnableAsync(id);

            // cleanup
            try { System.IO.File.Delete(tempZip); Directory.Delete(extractDir, recursive: true); } catch { }

            return Ok(new { ok = true, id });
        }
        catch (Exception e)
        {
            return BadRequest(new { error = e.Message });
        }
    }

public record InstallRequest(string Url);
}
