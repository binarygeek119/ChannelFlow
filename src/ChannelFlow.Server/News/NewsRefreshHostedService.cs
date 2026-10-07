using Microsoft.Extensions.DependencyInjection;
using FinTv.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed class NewsRefreshHostedService : BackgroundService
{
    private readonly NewsHeadlineService _headlines;
    private readonly IServiceScopeFactory _scopes;
    private readonly DatabaseInitializer _database;
    private readonly ILogger<NewsRefreshHostedService> _logger;

    public NewsRefreshHostedService(
        NewsHeadlineService headlines,
        IServiceScopeFactory scopes,
        DatabaseInitializer database,
        ILogger<NewsRefreshHostedService> logger)
    {
        _headlines = headlines;
        _scopes = scopes;
        _database = database;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _database.WaitUntilReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await SafeRefreshAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = 10;
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FinTv.Data.FinTvDbContext>();
                var settings = await db.NewsSettings.AsNoTracking()
                    .OrderBy(s => s.Id)
                    .FirstOrDefaultAsync(stoppingToken);
                if (settings is not null)
                {
                    minutes = Math.Clamp(settings.RefreshMinutes, 2, 120);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read news refresh interval");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await SafeRefreshAsync(stoppingToken);
        }
    }

    private async Task SafeRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var articles = await _headlines.RefreshAsync(cancellationToken);
            _logger.LogInformation("News headlines refreshed ({Count} articles)", articles.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Periodic news headline refresh failed");
        }
    }
}
