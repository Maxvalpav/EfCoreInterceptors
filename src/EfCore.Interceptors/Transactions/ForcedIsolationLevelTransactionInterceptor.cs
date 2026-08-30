using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Transactions;

/// <summary>
/// Forces every transaction (including the implicit ones around SaveChanges) to start with a
/// specific isolation level by taking over transaction creation. Useful for compliance scenarios
/// that require e.g. Snapshot/Serializable everywhere.
/// Mind the provider: SQL Server honors all levels; SQLite ignores them; PostgreSQL maps some.
/// </summary>
public class ForcedIsolationLevelTransactionInterceptor(IsolationLevel isolationLevel) : DbTransactionInterceptor
{
    private readonly IsolationLevel _isolationLevel = isolationLevel;

    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        => StartForced(connection);

    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
#if NET8_0_OR_GREATER
        var transaction = await connection.BeginTransactionAsync(_isolationLevel, cancellationToken);
        return InterceptionResult<DbTransaction>.SuppressWithResult(transaction);
#else
        return StartForced(connection);
#endif
    }

    private InterceptionResult<DbTransaction> StartForced(DbConnection connection)
        => InterceptionResult<DbTransaction>.SuppressWithResult(connection.BeginTransaction(_isolationLevel));
}
