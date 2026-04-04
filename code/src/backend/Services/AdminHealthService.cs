using Repere.Api.Db;
using Repere.Api.Models;

namespace Repere.Api.Services;

public sealed class AdminHealthService
{
    private readonly ISystemPool _systemPool;
    private readonly IDataPool _dataPool;
    private readonly DatabaseConfig _databaseConfig;

    public AdminHealthService(
        ISystemPool systemPool,
        IDataPool dataPool,
        DatabaseConfig databaseConfig)
    {
        _systemPool = systemPool;
        _dataPool = dataPool;
        _databaseConfig = databaseConfig;
    }

    public async Task<AdminHealthResult> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var systemConnection = await _systemPool.GetConnectionAsync();
            using var dataConnection = await _dataPool.GetConnectionAsync();

            var response = new AdminHealthResponse(
                Status: "healthy",
                Database: _databaseConfig.Provider.ToString().ToLowerInvariant(),
                SystemPool: systemConnection.State.ToString().ToLowerInvariant(),
                DataPool: dataConnection.State.ToString().ToLowerInvariant());

            return new AdminHealthResult(IsHealthy: true, Response: response);
        }
        catch (Exception ex)
        {
            var response = new AdminHealthResponse(
                Status: "unhealthy",
                Database: _databaseConfig.Provider.ToString().ToLowerInvariant(),
                SystemPool: "unavailable",
                DataPool: "unavailable",
                Error: ex.Message);

            return new AdminHealthResult(IsHealthy: false, Response: response);
        }
    }
}
