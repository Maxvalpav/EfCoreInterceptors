using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Emits OpenTelemetry-friendly metrics for every EF command:
/// histogram <c>ef.command.duration</c> (ms; tags: operation, outcome) and counters
/// <c>ef.command.executed</c> / <c>ef.command.failed</c>. Wire an OTLP/MeterListener to export.
/// </summary>
public class MetricsCommandInterceptor : DbCommandInterceptor
{
    private readonly Meter _meter;
    private readonly Histogram<double> _durationMs;
    private readonly Counter<long> _executed;
    private readonly Counter<long> _failed;

    public MetricsCommandInterceptor(string? meterName = null, string? version = null)
    {
        _meter = meterName is null && version is null
            ? Observability.SharedMeter.Meter
            : new Meter(meterName ?? "EfCore.Interceptors", version ?? "1.0.0");
        // OTel semconv: db.client.operation.duration in seconds (api-design-audit #7), keep ms compat as secondary
        _durationMs = _meter.CreateHistogram<double>("ef.command.duration", unit: "ms");
        _executed = _meter.CreateCounter<long>("ef.command.executed");
        _failed = _meter.CreateCounter<long>("ef.command.failed");
        // Secondary OTel histogram in seconds for semconv compliance
        _durationS = _meter.CreateHistogram<double>("db.client.operation.duration", unit: "s");
    }
    private readonly Histogram<double> _durationS;

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Record(eventData);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Record(eventData);
        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Record(eventData);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Record(eventData);
        return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Record(eventData);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override async ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        Record(eventData);
        return await base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        _failed.Add(1, new KeyValuePair<string, object?>("operation", eventData.ExecuteMethod.ToString()));
        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        CommandFailed(command, eventData);
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    private void Record(CommandExecutedEventData eventData)
    {
        var tag = new KeyValuePair<string, object?>("operation", eventData.ExecuteMethod.ToString());
        _durationMs.Record(Math.Max(0.01, eventData.Duration.TotalMilliseconds), tag);
        _durationS.Record(Math.Max(0.00001, eventData.Duration.TotalSeconds), tag);
        _executed.Add(1, tag);
    }
}
