using System.Diagnostics.Metrics;

namespace EfCore.Interceptors.Observability;

internal static class SharedMeter
{
    public static readonly Meter Meter = new("EfCore.Interceptors", "1.0.0");

#if NET9_0_OR_GREATER
    // Explicit buckets for the SQL-relevant range 0.5ms..10s (08.5): default .NET
    // buckets smear P95/P99. Net8 target uses runtime defaults (InstrumentAdvice is net9+).
    private static readonly InstrumentAdvice<double> SqlBucketsMs = new()
    {
        HistogramBucketBoundaries = [0.5, 1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000]
    };
    private static readonly InstrumentAdvice<double> SqlBucketsS = new()
    {
        HistogramBucketBoundaries = [0.0005, 0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10]
    };
#endif

    /// <summary>Duration histogram with SQL-tuned buckets on net9+ (08.5).</summary>
    internal static Histogram<double> DurationHistogram(Meter meter, string name, string unit, string? description = null)
#if NET9_0_OR_GREATER
        => meter.CreateHistogram<double>(name, unit, description,
            advice: unit == "ms" ? SqlBucketsMs : SqlBucketsS);
#else
        => meter.CreateHistogram<double>(name, unit, description);
#endif

    // Outbox processor (05.4). Low-cardinality tags only: no payload, no ids.
    public static readonly Counter<long> OutboxClaimed =
        Meter.CreateCounter<long>("ef.outbox.claimed", description: "Outbox messages claimed for delivery.");
    public static readonly Counter<long> OutboxDelivered =
        Meter.CreateCounter<long>("ef.outbox.delivered", description: "Outbox messages delivered to handler.");
    public static readonly Counter<long> OutboxFailed =
        Meter.CreateCounter<long>("ef.outbox.failed", description: "Outbox delivery attempts failed.");
    public static readonly Counter<long> OutboxDeadLettered =
        Meter.CreateCounter<long>("ef.outbox.dead_lettered", description: "Outbox messages parked in dead-letter queue.");
    public static readonly Histogram<double> OutboxBatchDuration =
        DurationHistogram(Meter, "ef.outbox.batch.duration", "s",
            "Outbox batch processing duration in seconds.");
    public static readonly Histogram<double> OutboxLag =
        DurationHistogram(Meter, "ef.outbox.lag", "s",
            "Outbox lag in seconds (now - oldest pending OccurredAtUtc).");

    // Concurrency retry (05.8).
    public static readonly Counter<long> SaveChangesRetries =
        Meter.CreateCounter<long>("ef.savechanges.retries", description: "SaveChanges concurrency retries.");

    // Second-level cache (06.5).
    public static readonly Counter<long> CacheHits =
        Meter.CreateCounter<long>("ef.cache.hits", description: "Second-level cache hits.");
    public static readonly Counter<long> CacheMisses =
        Meter.CreateCounter<long>("ef.cache.misses", description: "Second-level cache misses.");
    public static readonly Counter<long> CacheEntriesRejected =
        Meter.CreateCounter<long>("ef.cache.entry_rejected", description: "Cache entries rejected by size limits.");
    public static readonly Histogram<double> CacheServeDuration =
        DurationHistogram(Meter, "ef.cache.serve_duration", "s",
            "Time to serve a query result from cache in seconds.");

    // Audit §4.5 stable names (aliases over the instruments above; no raw values in tags).
    public static readonly Counter<long> InterceptorCommandsTotal =
        Meter.CreateCounter<long>("ef.interceptor.commands.total", description: "Total EF commands observed.");
    public static readonly Histogram<double> InterceptorCommandDuration =
        DurationHistogram(Meter, "ef.interceptor.command.duration", "s",
            "EF command duration in seconds.");
    public static readonly System.Diagnostics.Metrics.UpDownCounter<long> InterceptorOutboxPending =
        Meter.CreateUpDownCounter<long>("ef.interceptor.outbox.pending", description: "Outbox messages claimed but not yet resolved.");
    public static readonly Counter<long> InterceptorOutboxDeliveryFailures =
        Meter.CreateCounter<long>("ef.interceptor.outbox.delivery_failures", description: "Outbox delivery failures.");
    public static readonly Counter<long> InterceptorTenantDenied =
        Meter.CreateCounter<long>("ef.interceptor.tenant_denied", description: "Cross-tenant operations rejected.");

    private static long _auditCacheHits;
    private static long _auditCacheMisses;

    static SharedMeter()
    {
        Meter.CreateObservableGauge("ef.interceptor.cache.hit_ratio",
            () =>
            {
                var hits = System.Threading.Interlocked.Read(ref _auditCacheHits);
                var misses = System.Threading.Interlocked.Read(ref _auditCacheMisses);
                var total = hits + misses;
                return total == 0 ? 0.0 : (double)hits / total;
            },
            description: "Second-level cache hit ratio.");
    }

    /// <summary>Audit-alias cache recording: keeps legacy counters and feeds hit_ratio.</summary>
    public static void RecordCacheHit()
    {
        System.Threading.Interlocked.Increment(ref _auditCacheHits);
    }

    /// <summary>Audit-alias cache recording: keeps legacy counters and feeds hit_ratio.</summary>
    public static void RecordCacheMiss()
    {
        System.Threading.Interlocked.Increment(ref _auditCacheMisses);
    }
}
