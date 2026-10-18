using Microsoft.AspNetCore.Mvc;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/about")]
public class AboutController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    public AboutController(IWebHostEnvironment env) => _env = env;

    [HttpGet]
    public IActionResult Get()
    {
        var asm = typeof(Program).Assembly;
        var ver = asm.GetName().Version?.ToString() ?? "3.0.0";
        var isDocker = System.IO.File.Exists("/.dockerenv");
        return Ok(new
        {
            app = new
            {
                author = "binarygeek119",
                authorUrl = "https://github.com/binarygeek119",
                version = ver,
                revision = Environment.GetEnvironmentVariable("GIT_SHA") ?? "",
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                packagingLabel = isDocker ? "Docker" : "Non-Docker",
                homepage = "https://github.com/binarygeek119/ChannelFlow"
            },
            system = new
            {
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                machineName = Environment.MachineName,
                processorCount = Environment.ProcessorCount,
                workingSet = Environment.WorkingSet,
                timeZone = TimeZoneInfo.Local.Id,
                configFolder = _env.ContentRootPath,
                listenPort = 0
            },
            stream = new { }
        });
    }
}
