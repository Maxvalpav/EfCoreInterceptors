using System.Data.Common;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Observability;

/// <summary>
/// Connection metrics: counters <c>ef.connection.opened/closed/failed</c>
/// and histogram <c>ef.connection.open_duration</c> (ms).
/// </summary>
public class ConnectionMetricsInterceptor : DbConnectionInterceptor
{
    private static readonly Counter<long> StaticOpened = SharedMeter.Meter.CreateCounter<long>("ef.connection.opened");
    private static readonly Counter<long> StaticClosed = SharedMeter.Meter.CreateCounter<long>("ef.connection.closed");
    private static readonly Counter<long> StaticFailed = SharedMeter.Meter.CreateCounter<long>("ef.connection.failed");
    private static readonly Histogram<double> StaticDuration = SharedMeter.Meter.CreateHistogram<double>("ef.connection.open_duration", unit: "ms");

    private readonly Counter<long> _opened = StaticOpened;
    private readonly Counter<long> _closed = StaticClosed;
    private readonly Counter<long> _failed = StaticFailed;
    private readonly Histogram<double> _openDurationMs = StaticDuration;

    public ConnectionMetricsInterceptor() { }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        _opened.Add(1);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ConnectionOpened(connection, eventData);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData)
    {
        _closed.Add(1);
        _openDurationMs.Record(Math.Max(0.01, eventData.Duration.TotalMilliseconds));
        base.ConnectionClosed(connection, eventData);
    }

    public override Task ConnectionClosedAsync(
        DbConnection connection, ConnectionEndEventData eventData)
    {
        ConnectionClosed(connection, eventData);
        return base.ConnectionClosedAsync(connection, eventData);
    }

    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData)
    {
        _failed.Add(1);
        base.ConnectionFailed(connection, eventData);
    }

    public override Task ConnectionFailedAsync(
        DbConnection connection, ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ConnectionFailed(connection, eventData);
        return base.ConnectionFailedAsync(connection, eventData, cancellationToken);
    }
}
