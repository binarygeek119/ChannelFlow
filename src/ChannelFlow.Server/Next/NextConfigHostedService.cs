namespace FinTv.Next;

/// <summary>
/// Keeps the ErsatzTV next configuration current: writes on startup and every few minutes
/// (playout windows rotate daily and appear without a restart), and clears the folder when
/// the integration is disabled. Channel topology/normalization changes still require a next
/// container restart, which ChannelFlow surfaces via its logs.
/// </summary>
public sealed class NextConfigHostedService : BackgroundService
{
    private readonly NextCoordinatorService _coordinator;
    private readonly ILogger<NextConfigHostedService> _logger;

    public NextConfigHostedService(
        NextCoordinatorService coordinator,
        ILogger<NextConfigHostedService> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (NextCoordinatorService.IsEnabled)
            {
                await _coordinator.WriteConfigurationAsync(cancellationToken);
            }
            else
            {
                _coordinator.ClearConfiguration();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh ErsatzTV next configuration");
        }
    }
}