using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Observability;

/// <summary>
/// Counts how many SQL commands each SaveChanges issues and warns above the threshold —
/// catches "implicit N+1" during saves (e.g. one UPDATE per collection element because of
/// missing batching or accidental relationship fixups).
/// </summary>
public class CommandsPerSaveDiagnosticInterceptor(
    int warnAbove = 10,
    ILoggerFactory? loggerFactory = null) : SaveChangesInterceptor,
    IDbCommandInterceptor
{
    private readonly int _warnAbove = warnAbove;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.CommandsPerSave") ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<DbContext, int> _commands = new();

    // ---- save boundaries ----

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Reset(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Reset(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Report(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Report(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Report(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Report(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    // ---- command counting ----

    public InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Count(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Count(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Count(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Count(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Count(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Count(eventData.Context);
        return ValueTask.FromResult(result);
    }

    // ---- helpers ----

    private void Reset(DbContext? context)
    {
        if (context is not null)
        {
            _commands[context] = 0;
        }
    }

    private void Count(DbContext? context)
    {
        if (context is not null)
        {
            _commands.AddOrUpdate(context, 1, (_, existing) => existing + 1);
        }
    }

    protected virtual void Report(DbContext? context)
    {
        if (context is null || !_commands.TryRemove(context, out var count))
        {
            return;
        }

        if (count > _warnAbove)
        {
            _logger.LogWarning(
                "SaveChanges issued {Commands} commands (threshold {Threshold}). " +
                "Possible per-row statements inside one save.",
                count, _warnAbove);
        }
    }
}
