using System.Threading.Channels;

namespace ChannelFlow.Core;

public class BuildRequest
{
    public string ChannelId { get; set; } = "";
    public Func<CancellationToken, Task> Work { get; set; } = _ => Task.CompletedTask;
}

public class BuildQueue : BackgroundService
{
    private readonly Channel<BuildRequest> _queue = Channel.CreateUnbounded<BuildRequest>();
    private readonly ILogger<BuildQueue> _logger;

    public BuildQueue(ILogger<BuildQueue> logger) => _logger = logger;

    public ValueTask EnqueueAsync(BuildRequest req) => _queue.Writer.WriteAsync(req);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var req in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                _logger.LogInformation("Processing build for {Channel}", req.ChannelId);
                await req.Work(stoppingToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Build failed for {Channel}", req.ChannelId);
            }
        }
    }
}
