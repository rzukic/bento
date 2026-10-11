using Bento.Api.Services.Import;

namespace Bento.Api.Workers;

/// <summary>Runs saved imports whose time has come (Step 5.1): daily at their time or every few hours.</summary>
public sealed class ImportScheduler(ImportSourceService sources, ILogger<ImportScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var id in await sources.DueAsync())
                    await sources.RunScheduledAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "The scheduled imports failed.");
            }
        }
    }
}
