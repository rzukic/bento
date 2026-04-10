using Repere.Api.Db;
using Repere.Api.Models;
using System.Data;
using Repere.Api.Configuration;

namespace Repere.Api.Services;

public sealed class AdminHealthService
{
    private readonly ISystemPool _systemPool;
    private readonly IDataPool _dataPool;
    private readonly BackendRuntimeInfo _runtimeInfo;
    private readonly BackendRuntimeOptions _runtimeOptions;
    private readonly DatabaseConfig _databaseConfig;

    public AdminHealthService(
        ISystemPool systemPool,
        IDataPool dataPool,
        BackendRuntimeInfo runtimeInfo,
        BackendRuntimeOptions runtimeOptions,
        DatabaseConfig databaseConfig)
    {
        _systemPool = systemPool;
        _dataPool = dataPool;
        _runtimeInfo = runtimeInfo;
        _runtimeOptions = runtimeOptions;
        _databaseConfig = databaseConfig;
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

            return new AdminHealthResult(
                IsHealthy: true,
                Database: new AdminHealthDatabaseStatus(systemPoolStatus, dataPoolStatus),
                Runtime: BuildRuntimeStatus(),
                Uptime: FormatUptime(DateTimeOffset.UtcNow - _runtimeInfo.StartedAtUtc),
                Version: _runtimeInfo.Version);
        }
        catch (Exception ex)
        {
            return new AdminHealthResult(
                IsHealthy: false,
                Database: new AdminHealthDatabaseStatus(systemPoolStatus, dataPoolStatus),
                Runtime: BuildRuntimeStatus(),
                Uptime: FormatUptime(DateTimeOffset.UtcNow - _runtimeInfo.StartedAtUtc),
                Version: _runtimeInfo.Version,
                Error: ex.Message);
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

    private AdminHealthRuntimeStatus BuildRuntimeStatus()
    {
        return new AdminHealthRuntimeStatus(
            DatabaseProvider: _databaseConfig.Provider.ToString().ToLowerInvariant(),
            AuthMode: _runtimeOptions.AuthMode.ToString().ToLowerInvariant(),
            LogLevel: _runtimeOptions.LogLevel,
            DataDir: _runtimeOptions.DataDir,
            ConfigPath: _runtimeOptions.PersistentConfigPath,
            JwtConfigured: !string.IsNullOrWhiteSpace(_runtimeOptions.Jwt.Secret));
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
