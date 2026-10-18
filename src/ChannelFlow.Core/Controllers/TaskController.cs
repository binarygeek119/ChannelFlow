using Microsoft.AspNetCore.Mvc;

namespace ChannelFlow.Core.Controllers;

[ApiController]
[Route("api/tasks")]
public class TaskController : ControllerBase
{
    private readonly TaskTracker _tracker;
    public TaskController(TaskTracker tracker) => _tracker = tracker;

    [HttpGet]
    public IActionResult List() => Ok(_tracker.GetAll());

    [HttpGet("running")]
    public IActionResult Running() => Ok(_tracker.GetRunning());

    [HttpPost("clear")]
    public IActionResult Clear() { _tracker.ClearFinished(); return Ok(new { ok = true }); }
}
