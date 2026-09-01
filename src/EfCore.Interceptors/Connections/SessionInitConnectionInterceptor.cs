using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Connections;

/// <summary>
/// Runs a configured list of SQL statements every time a connection is opened —
/// session-scoped settings that cannot be expressed in the connection string:
/// <list type="bullet">
///   <item>SQL Server: SET TRANSACTION ISOLATION LEVEL ...; EXEC sp_set_session_context 'TenantId', @p;</item>
///   <item>PostgreSQL/Npgsql: SET search_path TO ...; SET application_name = ...;</item>
///   <item>SQLite: PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;</item>
/// </list>
/// Statements execute outside any EF pipeline (plain DbCommand), so they must be valid
/// for the target provider. SECURITY: do not interpolate user input via string concatenation — use parameterized
/// resolver or DbCommand parameters to avoid session-level SQL injection (security-audit #12).
/// </summary>
public class SessionInitConnectionInterceptor : DbConnectionInterceptor
{
    private readonly Func<DbContext?, IEnumerable<string>> _statementResolver;
    private readonly ILogger _logger;

    /// <summary>Static statement list — same for every connection (snapshot).</summary>
    public SessionInitConnectionInterceptor(
        IEnumerable<string> statements,
        ILoggerFactory? loggerFactory = null)
        : this(_ => statements?.ToArray() ?? Array.Empty<string>(), loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(statements);
    }

    /// <summary>
    /// Dynamic statements resolved per open (e.g. interpolate the current tenant id into
    /// sp_set_session_context). The resolver must be thread-safe.
    /// </summary>
    public SessionInitConnectionInterceptor(
        Func<DbContext?, IEnumerable<string>> statementResolver,
        ILoggerFactory? loggerFactory = null)
    {
        _statementResolver = statementResolver;
        _logger = loggerFactory?.CreateLogger("EfCore.Interceptors.SessionInit") ?? NullLogger.Instance;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        RunStatements(eventData.Context, connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await RunStatementsAsync(eventData.Context, connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    protected virtual IEnumerable<string> ResolveStatements(DbContext? context) => _statementResolver(context);

    protected virtual void RunStatements(DbContext? context, DbConnection connection)
    {
        var statements = ResolveStatements(context) ?? Array.Empty<string>();
        foreach (var statement in statements)
        {
            if (string.IsNullOrWhiteSpace(statement)) continue;
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session init statement failed: {Statement}", statement);
                throw new InvalidOperationException($"Session init failed for statement: {statement}", ex);
            }
        }
    }

    protected virtual async Task RunStatementsAsync(
        DbContext? context, DbConnection connection, CancellationToken cancellationToken)
    {
        var statements = ResolveStatements(context) ?? Array.Empty<string>();
        foreach (var statement in statements)
        {
            if (string.IsNullOrWhiteSpace(statement)) continue;
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session init statement failed: {Statement}", statement);
                throw new InvalidOperationException($"Session init failed for statement: {statement}", ex);
            }
        }
    }
}
