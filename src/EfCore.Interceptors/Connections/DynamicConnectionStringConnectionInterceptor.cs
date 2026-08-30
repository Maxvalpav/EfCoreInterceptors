using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Connections;

/// <summary>
/// Resolves the effective connection string at open time via a callback (e.g. database-per-tenant
/// routing, read/write split, dynamic failover targets) and re-points the connection before it opens.
/// The callback must be thread-safe.
/// <para><b>Pooling warning:</b> mutating <c>DbConnection.ConnectionString</c> on pooled connections (Npgsql/SqlClient)
/// poisons the pool — the connection remains keyed to the original string. Disable pooling or use
/// <c>DbDataSource</c> routing instead for tenant-per-database scenarios.</para>
/// </summary>
public partial class DynamicConnectionStringConnectionInterceptor(
    Func<DbContext?, string> connectionStringResolver,
    ILoggerFactory? loggerFactory = null) : DbConnectionInterceptor
{
    private readonly Func<DbContext?, string> _resolver = connectionStringResolver;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.ConnectionRouting") ?? NullLogger.Instance;

    public override InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Apply(eventData.Context, connection);
        return base.ConnectionOpening(connection, eventData, result);
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context, connection);
        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }

    protected virtual void Apply(DbContext? context, DbConnection connection)
    {
        var target = _resolver(context);

        if (string.IsNullOrWhiteSpace(target) || string.Equals(connection.ConnectionString, target, StringComparison.Ordinal))
        {
            return;
        }

        if (connection.State == ConnectionState.Open)
        {
            _logger.LogWarning(
                "Cannot reroute an already-open connection to '{Target}'; keeping current route.",
                Mask(target));
            return;
        }

        _logger.LogDebug("Routing {Connection} -> {Target}", connection.GetType().Name, Mask(target));
        connection.ConnectionString = target;
    }

    private static string Mask(string connectionString)
        => RegexSecrets().Replace(connectionString, "$1=***");

    [GeneratedRegex(@"(?i)\b(password|pwd|secret|token)\s*=\s*[^;]*")]
    private static partial Regex RegexSecrets();
}
