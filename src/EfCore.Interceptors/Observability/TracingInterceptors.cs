using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Observability;

/// <summary>
/// OpenTelemetry tracing via ActivitySource: wraps SaveChanges/Command/Transaction pipelines in Activities.
/// Export via AddOpenTelemetry().WithTracing(b => b.AddSource("EfCore.Interceptors")).
/// </summary>
public static class EfCoreTracing
{
    public static readonly ActivitySource Source = new("EfCore.Interceptors", "1.0.0");
}

public class TracingSaveChangesInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, ActivityHolder> _activities = new();
    private sealed class ActivityHolder(Activity activity) { public Activity Activity { get; } = activity; }

    private void StartSaveActivity(DbContext? context)
    {
        if (context is null) return;
        var activity = EfCoreTracing.Source.StartActivity("ef.save", ActivityKind.Internal);
        if (activity is null) return;
        activity.SetTag("db.context", context.GetType().Name);
        _activities.Remove(context);
        _activities.Add(context, new ActivityHolder(activity));
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        StartSaveActivity(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        StartSaveActivity(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Complete(eventData.Context, success: true);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Complete(eventData.Context, success: true);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Complete(eventData.Context, success: false, eventData.Exception);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Complete(eventData.Context, success: false, eventData.Exception);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void Complete(DbContext? context, bool success, Exception? ex = null)
    {
        if (context is null || !_activities.TryGetValue(context, out var holder))
        {
            return;
        }

        _activities.Remove(context);
        var activity = holder.Activity;
        activity.SetTag("ef.save.success", success);
        if (ex is not null)
        {
            activity.SetTag("exception.type", ex.GetType().Name);
            activity.SetTag("exception.message", ex.Message);
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        }

        activity.Dispose();
    }
}

public class TracingCommandInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    private readonly ConcurrentDictionary<System.Data.Common.DbCommand, Activity> _activities = new();

    public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
        System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
    {
        StartActivity(command, "ef.command.reader");
        return base.ReaderExecuting(command, eventData, result);
    }

    public override System.Data.Common.DbDataReader ReaderExecuted(
        System.Data.Common.DbCommand command, CommandExecutedEventData eventData, System.Data.Common.DbDataReader result)
    {
        FinishActivity(command, eventData.Duration, success: true);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
    {
        StartActivity(command, "ef.command.reader");
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
        System.Data.Common.DbCommand command, CommandExecutedEventData eventData, System.Data.Common.DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        FinishActivity(command, eventData.Duration, success: true);
        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        StartActivity(command, "ef.command.nonquery");
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override int NonQueryExecuted(
        System.Data.Common.DbCommand command, CommandExecutedEventData eventData, int result)
    {
        FinishActivity(command, eventData.Duration, success: true);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        StartActivity(command, "ef.command.nonquery");
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<int> NonQueryExecutedAsync(System.Data.Common.DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        FinishActivity(command, eventData.Duration, success: true);
        return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        StartActivity(command, "ef.command.scalar");
        return base.ScalarExecuting(command, eventData, result);
    }

    public override object? ScalarExecuted(
        System.Data.Common.DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        FinishActivity(command, eventData.Duration, success: true);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        StartActivity(command, "ef.command.scalar");
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<object?> ScalarExecutedAsync(System.Data.Common.DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        FinishActivity(command, eventData.Duration, success: true);
        return await base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override void CommandFailed(System.Data.Common.DbCommand command, CommandErrorEventData eventData)
    {
        FinishActivity(command, eventData.Duration, success: false, eventData.Exception);
        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(System.Data.Common.DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        FinishActivity(command, eventData.Duration, success: false, eventData.Exception);
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    private void StartActivity(System.Data.Common.DbCommand command, string name)
    {
        var activity = EfCoreTracing.Source.StartActivity(name, ActivityKind.Client);
        if (activity is null) return;
        var sql = command.CommandText ?? string.Empty;
        if (sql.Length > 200) sql = sql[..200];
        activity.SetTag("db.statement", sql);
        _activities[command] = activity;
    }

    private void FinishActivity(System.Data.Common.DbCommand command, TimeSpan duration, bool success, Exception? ex = null)
    {
        if (!_activities.TryRemove(command, out var activity)) return;
        activity.SetTag("db.duration_ms", duration.TotalMilliseconds);
        activity.SetTag("otel.status_code", success ? "OK" : "ERROR");
        if (ex is not null)
        {
            activity.SetTag("exception.type", ex.GetType().Name);
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        }

        activity.Dispose();
    }
}
