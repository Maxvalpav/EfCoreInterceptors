using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Commands;

/// <summary>Policy for bulk operations that bypass SaveChanges interceptors.</summary>
public enum BulkOperationPolicy
{
    /// <summary>Throw BulkOperationBlockedException before the command reaches DB.</summary>
    Throw,
    /// <summary>Log a warning and allow the command.</summary>
    Warn,
    /// <summary>Ignore — allow silently (default for non-guarded contexts).</summary>
    Allow
}

/// <summary>
/// Level-1 guard for <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> which bypass all
/// <see cref="Microsoft.EntityFrameworkCore.Diagnostics.ISaveChangesInterceptor"/>s (soft-delete, encryption, audit, guards).
/// Inspects <see cref="CommandSource.BulkUpdate"/> / <see cref="CommandSource.ExecuteDelete"/> / <see cref="CommandSource.ExecuteUpdate"/>
/// and blocks/warns when the SQL touches a guarded table (ISoftDeletableEntity, IProtectedEntity, IImmutableEntity, [Encrypted], ITenantEntity).
/// </summary>
public class BulkOperationGuardInterceptor : DbCommandInterceptor
{
    private readonly BulkOperationPolicy _policy;
    private readonly ILogger _logger;
    private readonly Func<DbContext?, IReadOnlySet<string>> _guardedTablesResolver;
    private IReadOnlySet<string>? _cachedGuardedTables;

    public BulkOperationGuardInterceptor(
        BulkOperationPolicy policy = BulkOperationPolicy.Throw,
        ILoggerFactory? loggerFactory = null,
        Func<DbContext?, IReadOnlySet<string>>? guardedTablesResolver = null)
    {
        _policy = policy;
        _logger = loggerFactory?.CreateLogger("EfCore.Interceptors.BulkGuard") ?? NullLogger.Instance;
        _guardedTablesResolver = guardedTablesResolver ?? DefaultGuardedTables;
    }

    public BulkOperationGuardInterceptor(
        BulkOperationPolicy policy,
        IReadOnlySet<string> guardedTables,
        ILoggerFactory? loggerFactory = null)
    {
        _policy = policy;
        _logger = loggerFactory?.CreateLogger("EfCore.Interceptors.BulkGuard") ?? NullLogger.Instance;
        _cachedGuardedTables = guardedTables;
        _guardedTablesResolver = _ => guardedTables;
    }

    private static IReadOnlySet<string> DefaultGuardedTables(DbContext? context)
    {
        if (context is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var et in context.Model.GetEntityTypes())
        {
            var clr = et.ClrType;
            if (typeof(Abstractions.ISoftDeletableEntity).IsAssignableFrom(clr)
                || typeof(Abstractions.IProtectedEntity).IsAssignableFrom(clr)
                || typeof(Abstractions.IImmutableEntity).IsAssignableFrom(clr)
                || typeof(Abstractions.ITenantEntity).IsAssignableFrom(clr)
                || HasEncryptedProperty(et))
            {
                var table = et.GetTableName();
                if (!string.IsNullOrEmpty(table)) tables.Add(table!);
                // Also add view/table via schema-qualified name
                var schema = et.GetSchema();
                if (!string.IsNullOrEmpty(schema) && !string.IsNullOrEmpty(table))
                    tables.Add($"{schema}.{table}");
            }
        }
        return tables;
    }

    private static bool HasEncryptedProperty(Microsoft.EntityFrameworkCore.Metadata.IEntityType et)
    {
        foreach (var prop in et.GetProperties())
        {
            if (prop.PropertyInfo?.GetCustomAttributes(typeof(Abstractions.EncryptedAttribute), true).Length > 0)
                return true;
        }
        return false;
    }

    private IReadOnlySet<string> GuardedTables(DbContext? ctx)
    {
        if (_cachedGuardedTables is not null) return _cachedGuardedTables;
        // Cache per-model to avoid recomputing on every command; context.Model is stable.
        return DefaultGuardedTables(ctx);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData, command.CommandText);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Guard(eventData, command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Guard(CommandEventData eventData, string sql)
    {
        if (eventData.CommandSource is not (CommandSource.BulkUpdate or CommandSource.ExecuteDelete or CommandSource.ExecuteUpdate))
        {
#pragma warning disable CS0618
            if (eventData.CommandSource != CommandSource.BulkUpdate) return;
#pragma warning restore CS0618
        }

        var guarded = GuardedTables(eventData.Context);
        if (guarded.Count == 0) return;

        string? matched = null;
        foreach (var table in guarded)
        {
            if (sql.Contains(table, StringComparison.OrdinalIgnoreCase))
            {
                matched = table;
                break;
            }
        }

        if (matched is null) return;

        var msg = $"Bulk operation '{eventData.CommandSource}' touches guarded table '{matched}' and bypasses SaveChanges interceptors (soft-delete/encryption/audit/guards). " +
                  $"Use RemoveRange+SaveChanges or ExecuteSoftDeleteAsync helpers. SQL: {sql[..Math.Min(400, sql.Length)]}";

        switch (_policy)
        {
            case BulkOperationPolicy.Throw:
                throw new BulkOperationBlockedException(msg);
            case BulkOperationPolicy.Warn:
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("{Message}", msg);
                break;
        }
    }
}

/// <summary>Raised when a bulk operation touches a guarded table.</summary>
public sealed class BulkOperationBlockedException(string message) : InvalidOperationException(message);
