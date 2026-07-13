using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PlexRequestsHosted.Infrastructure.Data;

/// <summary>
/// Applies SQLite pragmas whenever a connection opens: WAL journal mode (readers don't block the
/// background writers — availability scan, series monitor, cache refresh), a busy timeout so brief
/// write contention retries instead of throwing SQLITE_BUSY, and synchronous=NORMAL (safe + faster
/// under WAL). WAL is persistent for the file; the others are per-connection, so we set them each open.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
