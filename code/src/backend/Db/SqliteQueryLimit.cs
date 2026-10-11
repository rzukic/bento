using System.Data;
using Microsoft.Data.Sqlite;

namespace Bento.Api.Db;

/// <summary>
/// SQLite runs a statement until it ends: a command timeout there only limits
/// waiting for a lock. Postgres stops a query after the command timeout and when
/// its request is cancelled; this gives SQLite the same — past the limit, or
/// when the browser no longer waits, the running statement is interrupted.
/// </summary>
public sealed class SqliteQueryLimit : IDisposable
{
    /// <summary>As Npgsql's default command timeout, so both providers stop at the same time.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource? _deadline;
    private readonly CancellationTokenRegistration _registration;
    private readonly CancellationToken _aborted;

    private SqliteQueryLimit(IDbConnection connection, TimeSpan limit, CancellationToken aborted)
    {
        _aborted = aborted;
        if (connection is not SqliteConnection { Handle: { } handle })
            return;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        _deadline.CancelAfter(limit);
        _registration = _deadline.Token.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle));
    }

    /// <summary>Watches the statements run on <paramref name="connection"/> until disposed; nothing on Postgres.</summary>
    public static SqliteQueryLimit Watch(IDbConnection connection, CancellationToken aborted, TimeSpan? limit = null) =>
        new(connection, limit ?? DefaultLimit, aborted);

    /// <summary>
    /// An interrupted statement as the error the rest of the server knows: a
    /// timeout (504), or a cancellation when the request was given up.
    /// </summary>
    public Exception? Translate(Exception exception)
    {
        if (exception is not SqliteException { SqliteErrorCode: 9 } || _deadline is null)
            return null;
        return _aborted.IsCancellationRequested
            ? new OperationCanceledException("The request was cancelled.", exception, _aborted)
            : new TimeoutException("The query ran longer than the time limit and was stopped.", exception);
    }

    public void Dispose()
    {
        _registration.Dispose();
        _deadline?.Dispose();
    }
}
