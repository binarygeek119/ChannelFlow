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
        LogStartupState();
        await RefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    /// <summary>
    /// next failing to engage is otherwise silent: no config folder is written, no resolver
    /// traffic appears, and playback quietly keeps using ChannelFlow's own encoder. Say which
    /// of the three states we are in at boot so a half-configured next is not mistaken for a
    /// working one.
    /// </summary>
    private void LogStartupState()
    {
        var next = NextCoordinatorService.Configured;
        if (next is not { Enabled: true })
        {
            _logger.LogInformation(
                "ErsatzTV next integration is disabled; streams are encoded by ChannelFlow.");
            return;
        }

        if (string.IsNullOrWhiteSpace(next.BaseUrl)
            || string.IsNullOrWhiteSpace(next.ResolverBaseUrl)
            || string.IsNullOrWhiteSpace(next.ResolverToken))
        {
            _logger.LogWarning(
                "ErsatzTV next is turned on but incomplete (BaseUrl {Base}, ResolverBaseUrl {Resolver}, token {Token}); "
                + "no configuration was written and playback stays on ChannelFlow's encoder.",
                string.IsNullOrWhiteSpace(next.BaseUrl) ? "missing" : "set",
                string.IsNullOrWhiteSpace(next.ResolverBaseUrl) ? "missing" : "set",
                string.IsNullOrWhiteSpace(next.ResolverToken) ? "missing" : "set");
            return;
        }

        _logger.LogInformation("ErsatzTV next integration is active; serving HLS through next.");
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