using Bento.Api.Services;

namespace Bento.Api.Workers;

/// <summary>Runs each price source once a day at its time (closing prices and new keys' history).</summary>
public sealed class PriceSourceScheduler(PriceSourceService sources, ILogger<PriceSourceScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var id in await sources.DueDailyAsync())
                    await sources.RunAsync(id, "daily", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "The daily price run failed.");
            }
        }
    }
}
