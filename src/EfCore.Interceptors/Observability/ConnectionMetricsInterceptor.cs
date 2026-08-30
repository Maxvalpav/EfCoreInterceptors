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
    private const string MeterName = "EfCore.Interceptors";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _opened;
    private readonly Counter<long> _closed;
    private readonly Counter<long> _failed;
    private readonly Histogram<double> _openDurationMs;

    public ConnectionMetricsInterceptor()
    {
        _opened = _meter.CreateCounter<long>("ef.connection.opened");
        _closed = _meter.CreateCounter<long>("ef.connection.closed");
        _failed = _meter.CreateCounter<long>("ef.connection.failed");
        _openDurationMs = _meter.CreateHistogram<double>("ef.connection.open_duration", unit: "ms");
    }

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
