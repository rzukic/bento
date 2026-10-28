using System.Collections.Concurrent;
using System.Threading.Channels;
using Bento.Api.Db.Schema;
using Bento.Api.Services.Notifications;
using Bento.Api.Services.Workspaces;

namespace Bento.Api.Services;

/// <summary>Who or what wrote rows (Step 5.12): the app, an API key, an MCP client, a public form, an import, a calendar.</summary>
/// <param name="Kind">app, api, mcp, form, import or calendar.</param>
/// <param name="Name">The user, the form, the import … for the message.</param>
public sealed record RowOrigin(string Kind, string? Name)
{
    public static readonly string[] Kinds = ["app", "api", "mcp", "form", "import", "calendar"];

    private static readonly AsyncLocal<RowOrigin?> Ambient = new();

    /// <summary>The origin of writes in this flow; the app when nothing said otherwise.</summary>
    public static RowOrigin Current => Ambient.Value ?? new RowOrigin("app", null);

    /// <summary>Writes inside the scope come from <paramref name="kind"/>.</summary>
    public static IDisposable Use(string kind, string? name)
    {
        var previous = Ambient.Value;
        Ambient.Value = new RowOrigin(kind, name);
        return new Restore(() => Ambient.Value = previous);
    }

    /// <summary>"by Anna", "through the public form “Signup”" — for a message.</summary>
    public string Text => (Kind, Name) switch
    {
        ("app", { } user) => $"by {user}",
        ("app", null) => "in the app",
        ("api", { } user) => $"through the API ({user}’s key)",
        ("api", null) => "through the API",
        ("mcp", { } client) => $"by {client} (MCP)",
        ("mcp", null) => "through MCP",
        ("form", { } form) => $"through the public form “{form}”",
        ("form", null) => "through a public form",
        ("import", { } import) => $"by the import “{import}”",
        ("import", null) => "by an import",
        ("calendar", { } device) => $"from a calendar ({device})",
        _ => "from a calendar"
    };

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>One written row: its key, and what it was and is (each when known).</summary>
/// <param name="Changed">Updates: the columns whose value changed.</param>
public sealed record RowChange(string Key, IDictionary<string, object?>? Before, IDictionary<string, object?>? After, IReadOnlyList<string>? Changed = null);

/// <summary>
/// Row events (Step 5.12): every write of rows is reported here and handled
/// off the request path. Changes are gathered for a few seconds per table,
/// operation and origin — an import of 300 rows is one event, not 300 — and
/// then handed to the notification rules that listen; conditions also look
/// again after data changed (at most once a minute per workspace).
/// </summary>
public sealed class RowEvents : BackgroundService
{
    public static readonly string[] Operations = ["created", "updated", "deleted"];
    private const int MaxRowsPerBatch = 5000;

    private readonly IServiceProvider _services;
    private readonly ILogger<RowEvents>? _logger;
    private readonly TimeSpan _delay;
    private readonly Channel<Pending> _queue = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<long, Listeners> _listeners = new();
    private readonly ConcurrentDictionary<long, DateTime> _lastDataCheck = new();
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private readonly Dictionary<BatchKey, Batch> _batches = new();
    private int _listenerVersion;

    public RowEvents(IServiceProvider services, IConfiguration? configuration = null, ILogger<RowEvents>? logger = null)
    {
        _services = services;
        _logger = logger;
        _delay = TimeSpan.FromSeconds(Math.Clamp(configuration?.GetValue<double?>("Notifications:RowEventDelaySeconds") ?? 5, 0, 300));
    }

    private sealed record Listeners(int Version, HashSet<(string Table, string Op)> Pairs);
    private sealed record Pending(Workspace Workspace, string Table, string Op, RowOrigin Origin, IReadOnlyList<RowChange> Rows);
    private sealed record BatchKey(long Workspace, string Table, string Op, string OriginKind, string? OriginName);

    private sealed class Batch(Pending first)
    {
        public Workspace Workspace { get; } = first.Workspace;
        public RowOrigin Origin { get; } = first.Origin;
        public DateTime Started { get; } = DateTime.UtcNow;
        public List<RowChange> Rows { get; } = [];
        public int Dropped { get; set; }
    }

    /// <summary>The notification rules changed: which tables are listened to is read again.</summary>
    public void Invalidate() => Interlocked.Increment(ref _listenerVersion);

    /// <summary>
    /// Whether a rule listens to <paramref name="op"/> on the table in this
    /// workspace — then a write reads what a row was before, or the keys of
    /// imported rows, which it otherwise does not need.
    /// </summary>
    public async Task<bool> WantsAsync(TableDef table, string op)
    {
        var version = Volatile.Read(ref _listenerVersion);
        var workspace = WorkspaceContext.Id;
        if (!_listeners.TryGetValue(workspace, out var listeners) || listeners.Version != version)
        {
            try
            {
                var rules = _services.GetRequiredService<NotificationRuleService>();
                listeners = new Listeners(version, (await rules.RowListenersAsync()).ToHashSet());
            }
            catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
            {
                _logger?.LogWarning(e, "The row event rules could not be read.");
                return false;
            }
            _listeners[workspace] = listeners;
        }
        return listeners.Pairs.Contains((table.Name.ToLowerInvariant(), op));
    }

    /// <summary>Rows were written: handled a little later, together with the rows written around the same time.</summary>
    public void Publish(TableDef table, string op, IReadOnlyList<RowChange> rows)
    {
        if (rows.Count == 0)
            return;
        _queue.Writer.TryWrite(new Pending(WorkspaceContext.Current, table.Name, op, RowOrigin.Current, rows));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(_delay.TotalMilliseconds / 4, 50, 1000));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(tick);
                try
                {
                    await _queue.Reader.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // A tick: batches that waited long enough go out.
                }
                await FlushAsync(force: false, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger?.LogError(e, "Row events could not be handled.");
            }
        }
    }

    /// <summary>Hands every waiting change to the rules now (tests, shutdown).</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => FlushAsync(force: true, cancellationToken);

    private async Task FlushAsync(bool force, CancellationToken cancellationToken)
    {
        await _flushing.WaitAsync(cancellationToken);
        try
        {
            while (_queue.Reader.TryRead(out var pending))
            {
                var key = new BatchKey(pending.Workspace.Id, pending.Table.ToLowerInvariant(), pending.Op, pending.Origin.Kind, pending.Origin.Name);
                if (!_batches.TryGetValue(key, out var batch))
                    _batches[key] = batch = new Batch(pending);
                var room = MaxRowsPerBatch - batch.Rows.Count;
                batch.Rows.AddRange(pending.Rows.Take(Math.Max(room, 0)));
                batch.Dropped += Math.Max(pending.Rows.Count - Math.Max(room, 0), 0);
            }
            var now = DateTime.UtcNow;
            var due = _batches.Where(b => force || now - b.Value.Started >= _delay).ToList();
            foreach (var (key, batch) in due)
            {
                _batches.Remove(key);
                await HandleAsync(key, batch, cancellationToken);
            }
            foreach (var workspace in due.Select(d => d.Value.Workspace).DistinctBy(w => w.Id))
                await DataChangedAsync(workspace, force, cancellationToken);
        }
        finally
        {
            _flushing.Release();
        }
    }

    private async Task HandleAsync(BatchKey key, Batch batch, CancellationToken cancellationToken)
    {
        using var inWorkspace = WorkspaceContext.Use(batch.Workspace);
        try
        {
            var rules = _services.GetRequiredService<NotificationRuleService>();
            await rules.OnRowsChangedAsync(key.Table, key.Op, batch.Rows, batch.Dropped, batch.Origin, cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning(e, "Rules on {Op} rows of {Table} could not be checked.", key.Op, key.Table);
        }
    }

    /// <summary>Conditions and targets look again after data changed — at most once a minute per workspace (tests: at once).</summary>
    private async Task DataChangedAsync(Workspace workspace, bool force, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (!force && _lastDataCheck.TryGetValue(workspace.Id, out var last) && now - last < TimeSpan.FromMinutes(1))
            return;
        _lastDataCheck[workspace.Id] = now;
        using var inWorkspace = WorkspaceContext.Use(workspace);
        try
        {
            await _services.GetRequiredService<NotificationRuleService>().OnDataChangedAsync(cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning(e, "Conditions could not be checked after a change.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await FlushAsync(force: true, cancellationToken);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            // Shutting down: what is left is not told.
        }
    }
}
