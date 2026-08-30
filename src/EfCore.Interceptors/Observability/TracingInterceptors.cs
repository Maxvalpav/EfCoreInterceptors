using System.Diagnostics;
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
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        using var a = EfCoreTracing.Source.StartActivity("ef.save", ActivityKind.Internal);
        a?.SetTag("db.context", eventData.Context?.GetType().Name);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        using var a = EfCoreTracing.Source.StartActivity("ef.save", ActivityKind.Internal);
        a?.SetTag("db.context", eventData.Context?.GetType().Name);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

public class TracingCommandInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    public override System.Data.Common.DbDataReader ReaderExecuted(
        System.Data.Common.DbCommand command, CommandExecutedEventData eventData, System.Data.Common.DbDataReader result)
    {
        using var a = EfCoreTracing.Source.StartActivity("ef.command", ActivityKind.Client);
        a?.SetTag("db.statement", command.CommandText[..Math.Min(200, command.CommandText.Length)]);
        a?.SetTag("db.duration_ms", eventData.Duration.TotalMilliseconds);
        return base.ReaderExecuted(command, eventData, result);
    }
}
