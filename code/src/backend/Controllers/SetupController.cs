using Dapper;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Configuration;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Models;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>
/// First-run setup surface consumed by the <c>/setup</c> wizard in the SPA.
/// </summary>
[ApiController]
[Route(ApiRoutes.Setup)]
public sealed class SetupController : ControllerBase
{
    private readonly BackendRuntimeOptions _runtimeOptions;
    private readonly BackendRuntimeInfo _runtimeInfo;
    private readonly DatabaseConfig _databaseConfig;
    private readonly ISystemPool _systemPool;
    private readonly ModelJsonStore _modelJsonStore;
    private readonly AuthService _authService;
    private readonly SchemaService _schemaService;
    private readonly SchemaCache _schemaCache;
    private readonly CrudService _crudService;

    public SetupController(
        BackendRuntimeOptions runtimeOptions,
        BackendRuntimeInfo runtimeInfo,
        DatabaseConfig databaseConfig,
        ISystemPool systemPool,
        ModelJsonStore modelJsonStore,
        AuthService authService,
        SchemaService schemaService,
        SchemaCache schemaCache,
        CrudService crudService)
    {
        _runtimeOptions = runtimeOptions;
        _runtimeInfo = runtimeInfo;
        _databaseConfig = databaseConfig;
        _systemPool = systemPool;
        _modelJsonStore = modelJsonStore;
        _authService = authService;
        _schemaService = schemaService;
        _schemaCache = schemaCache;
        _crudService = crudService;
    }

    /// <summary>
    /// Load the embedded demo workspace (customers, orders, products) into an
    /// empty installation. Admin only; rejected with 409 once any schema exists.
    /// </summary>
    [HttpPost("sample-data")]
    [RequireRole("admin")]
    [ProducesResponseType<SampleDataResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SampleDataResponse>> LoadSampleData(CancellationToken cancellationToken)
    {
        var result = await SampleDataLoader.LoadAsync(
            _modelJsonStore,
            _schemaService,
            _schemaCache,
            _crudService,
            cancellationToken);

        return Ok(new SampleDataResponse(
            SchemaVersion: result.SchemaVersion,
            TablesCreated: result.TablesCreated,
            RowsInserted: result.RowsInserted));
    }

    /// <summary>
    /// Whether this installation still needs first-run setup, plus the packaged
    /// runtime facts the wizard shows read-only. Anonymous on purpose: the wizard
    /// has to call it before any account exists. Never includes secrets or paths.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType<SetupStatusResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SetupStatusResponse>> GetStatus(CancellationToken cancellationToken)
    {
        var hasUsers = await HasAnyUsersAsync(cancellationToken);
        var registrationBootstrapOpen = await _authService.IsBootstrapSelfRegistrationOpenAsync(cancellationToken);
        var currentModel = await _modelJsonStore.GetCurrentAsync();
        var schemaVersion = currentModel?.Version ?? 0;

        // In open mode nobody has to register, so the installation counts as
        // initialised as soon as it runs. In standard mode the first admin
        // account is what completes setup (same check AuthService uses for the
        // bootstrap registration, so the two can never disagree).
        var initialized = _runtimeOptions.IsOpenAuthMode || hasUsers;

        return Ok(new SetupStatusResponse(
            Initialized: initialized,
            AuthMode: _runtimeOptions.AuthMode.ToString().ToLowerInvariant(),
            AuthRequired: _runtimeOptions.IsStandardAuthMode,
            RegistrationBootstrapOpen: registrationBootstrapOpen,
            DatabaseProvider: _databaseConfig.Provider.ToString().ToLowerInvariant(),
            SchemaVersion: schemaVersion,
            // The one-time sample data loader only works on an empty
            // installation; advertise it only while that is still the case.
            SampleDataAvailable: SampleDataLoader.IsAvailable(schemaVersion),
            Version: _runtimeInfo.Version));
    }

    private async Task<bool> HasAnyUsersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var usersTable = _systemPool.Provider == DatabaseProvider.Postgres ? "_system.users" : "users";
        using var conn = await _systemPool.GetConnectionAsync();
        var userCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {usersTable};");
        return userCount > 0;
    }
}
