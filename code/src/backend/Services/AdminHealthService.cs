using Repere.Api.Db;
using Repere.Api.Models;
using System.Data;

namespace Repere.Api.Services;

public sealed class AdminHealthService
{
    private readonly ISystemPool _systemPool;
    private readonly IDataPool _dataPool;
    private readonly BackendRuntimeInfo _runtimeInfo;

    public AdminHealthService(
        ISystemPool systemPool,
        IDataPool dataPool,
        BackendRuntimeInfo runtimeInfo)
    {
        _systemPool = systemPool;
        _dataPool = dataPool;
        _runtimeInfo = runtimeInfo;
    }

    public async Task<AdminHealthResult> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string systemPoolStatus = "unavailable";
        string dataPoolStatus = "unavailable";
        IDbConnection? systemConnection = null;
        IDbConnection? dataConnection = null;

        try
        {
            systemConnection = await _systemPool.GetConnectionAsync();
            systemPoolStatus = ToPoolStatus(systemConnection.State);

            dataConnection = await _dataPool.GetConnectionAsync();
            dataPoolStatus = ToPoolStatus(dataConnection.State);

            var response = new AdminHealthResponse(
                Status: "healthy",
                Database: new AdminHealthDatabaseStatus(systemPoolStatus, dataPoolStatus),
                Uptime: FormatUptime(DateTimeOffset.UtcNow - _runtimeInfo.StartedAtUtc),
                Version: _runtimeInfo.Version);

            return new AdminHealthResult(IsHealthy: true, Response: response);
        }
        catch (Exception ex)
        {
            var response = new AdminHealthResponse(
                Status: "unhealthy",
                Database: new AdminHealthDatabaseStatus(systemPoolStatus, dataPoolStatus),
                Uptime: FormatUptime(DateTimeOffset.UtcNow - _runtimeInfo.StartedAtUtc),
                Version: _runtimeInfo.Version,
                Error: ex.Message);

            return new AdminHealthResult(IsHealthy: false, Response: response);
        }
        finally
        {
            dataConnection?.Dispose();
            systemConnection?.Dispose();
        }
    }

    private static string ToPoolStatus(ConnectionState state)
    {
        return state == ConnectionState.Open
            ? "connected"
            : "unavailable";
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero)
        {
            uptime = TimeSpan.Zero;
        }

        var parts = new List<string>();
        if (uptime.Days > 0)
        {
            parts.Add($"{uptime.Days}d");
        }

        if (uptime.Hours > 0 || uptime.Days > 0)
        {
            parts.Add($"{uptime.Hours}h");
        }

        parts.Add($"{uptime.Minutes}m");

        return string.Join(" ", parts);
    }
}
