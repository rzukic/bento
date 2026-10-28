using Bento.Api.Services.Import;

namespace Bento.Api.Workers;

/// <summary>Runs saved imports whose time has come (Step 5.1): daily at their time or every few hours.</summary>
public sealed class ImportScheduler(ImportSourceService sources, ILogger<ImportScheduler> logger, Bento.Api.Services.Workspaces.WorkspaceService workspaces) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            IReadOnlyList<Bento.Api.Services.Workspaces.Workspace> all;
            try
            {
                all = await workspaces.ReloadAsync();
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "The workspaces could not be read.");
                continue;
            }
            // Each workspace in turn, in its own tables and calendar; one failing stops none of the others.
            foreach (var workspace in all)
            {
                using var inWorkspace = Bento.Api.Services.Workspaces.WorkspaceContext.Use(workspace);
                try
                {
                    foreach (var id in await sources.DueAsync())
                        await sources.RunScheduledAsync(id, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "The scheduled imports failed (workspace {Workspace}).", workspace.Slug);
                }
            }
        }
    }
}
