using Bento.Api.Services.Notifications;

namespace Bento.Api.Workers;

/// <summary>Checks notification rules whose time has come (Step 5.3): hourly ones each hour, daily ones at their time.</summary>
public sealed class NotificationRuleScheduler(NotificationRuleService rules, ILogger<NotificationRuleScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var id in await rules.DueAsync())
                    await rules.CheckScheduledAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "The notification rules could not be checked.");
            }
        }
    }
}
