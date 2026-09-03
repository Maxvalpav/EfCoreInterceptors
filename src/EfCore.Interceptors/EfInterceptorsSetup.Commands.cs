using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors;

// Grouped command builders (09.1: EfInterceptorsSetup split into partials).
public sealed partial class EfInterceptorsSetup
{
    /// <summary>Warns when commands exceed the configured duration threshold.</summary>
    public EfInterceptorsSetup WithSlowQueryWarning(
        TimeSpan threshold,
        ILoggerFactory? loggerFactory = null,
        Func<CommandEventData, bool>? filter = null)
        => Add(new SlowQueryCommandInterceptor(threshold, loggerFactory, filter));

    /// <summary>
    /// Appends provider hints to queries tagged via TagWith("key"),
    /// e.g. new Dictionary&lt;string,string&gt; { ["recompile"] = "OPTION (RECOMPILE)" } on SQL Server.
    /// </summary>
    public EfInterceptorsSetup WithQueryHints(IReadOnlyDictionary<string, string> hintsByTag)
        => Add(new QueryHintsCommandInterceptor(hintsByTag: hintsByTag));

    /// <summary>Appends hints selected by a custom predicate over the SQL text.</summary>
    public EfInterceptorsSetup WithQueryHints(Func<string, string?> hintSelector)
        => Add(new QueryHintsCommandInterceptor(hintSelector));

    /// <summary>PostgreSQL FOR UPDATE/SHARE locking hints via TagWith.</summary>
    public EfInterceptorsSetup WithPostgresHints(IReadOnlyDictionary<string, string>? hintsByTag = null)
        => Add(new QueryHintsCommandInterceptor(hintsByTag: hintsByTag ?? PostgresQueryHints.Defaults));

    /// <summary>Throws before any write statement reaches the database.</summary>
    public EfInterceptorsSetup WithReadOnlyGuard(Func<DbContext?, bool>? isEnabled = null)
        => Add(new ReadOnlyGuardCommandInterceptor(isEnabled));

    /// <summary>
    /// Logs every SQL command with duration. Options: include parameter values, redact sensitive
    /// fragments (PII/cards — see <c>PiiRedactor.Default</c>) and sample non-error logs
    /// (failures are always logged). Replaces any previously added SQL logger.
    /// </summary>
    public EfInterceptorsSetup WithSqlLogging(
        ILoggerFactory? loggerFactory = null,
        bool includeParameterValues = false,
        Func<string, string>? textRedactor = null,
        double sampleRate = 1.0)
    {
        _interceptors.RemoveAll(i => i is SqlLoggingCommandInterceptor);
        return Add(new SqlLoggingCommandInterceptor(loggerFactory, includeParameterValues, textRedactor, sampleRate));
    }

    /// <summary>
    /// Read-through in-memory query cache (same SQL + parameters served without a DB roundtrip).
    /// Writes evict only entries that read the written tables (06.4); entries expire after timeToLive.
    /// Oversize results bypass the store but are still served (06.2).
    /// </summary>
    public EfInterceptorsSetup WithSecondLevelCache(
        TimeSpan? timeToLive = null,
        bool skipInsideTransactions = true,
        int maxRowsPerEntry = 10_000,
        long maxBytesPerEntry = 8 * 1024 * 1024)
        => Add(new CachingCommandInterceptor(timeToLive, skipInsideTransactions, false, 1000, null, maxRowsPerEntry, maxBytesPerEntry));

    /// <summary>Second-level cache with custom store (e.g. Redis via DistributedQueryCacheStore).</summary>
    public EfInterceptorsSetup WithSecondLevelCache(
        IQueryCacheStore store,
        TimeSpan? timeToLive = null,
        bool skipInsideTransactions = true,
        bool invalidateOnWrites = false,
        int maxRowsPerEntry = 10_000,
        long maxBytesPerEntry = 8 * 1024 * 1024)
        => Add(new CachingCommandInterceptor(timeToLive, skipInsideTransactions, invalidateOnWrites, 1000, store, maxRowsPerEntry, maxBytesPerEntry));

    /// <summary>Distributed cache (IDistributedCache/Redis) for second-level cache.</summary>
    public EfInterceptorsSetup WithDistributedCache(Microsoft.Extensions.Caching.Distributed.IDistributedCache cache, TimeSpan? timeToLive = null)
        => Add(new CachingCommandInterceptor(timeToLive, true, false, 1000, new DistributedQueryCacheStore(cache, timeToLive)));

    /// <summary>HybridCache second-level cache (shared IMemoryCache).</summary>
    [Obsolete("No single-flight, no size limits, no table invalidation (06.7): use WithSecondLevelCache(...) instead.")]
    public EfInterceptorsSetup WithHybridCache(Microsoft.Extensions.Caching.Memory.IMemoryCache cache, TimeSpan? ttl = null, bool skipInsideTransactions = true)
        => Add(new HybridCacheCommandInterceptor(cache, ttl, skipInsideTransactions));

    /// <summary>Warns when one context repeats the identical SQL template N times (N+1 signature).</summary>
    public EfInterceptorsSetup WithNPlusOneDetection(int threshold = 5, ILoggerFactory? loggerFactory = null, bool captureStackTrace = false)
        => Add(new NPlusOneDetectorCommandInterceptor(threshold, loggerFactory, captureStackTrace));

    /// <summary>Publishes ef.command.duration/executed/failed metrics via System.Diagnostics.Metrics.</summary>
    public EfInterceptorsSetup WithCommandMetrics(string? meterName = null)
        => Add(new MetricsCommandInterceptor(meterName));

    /// <summary>Rejects write statements executed outside an explicit transaction.</summary>
    public EfInterceptorsSetup WithTransactionalWrites(Func<DbContext?, bool>? isEnabled = null)
        => Add(new WritesRequireTransactionCommandInterceptor(isEnabled));

    /// <summary>
    /// Per-command timeout from a selector over the SQL text — e.g. give TagWith("report")
    /// queries 300s while everything else keeps the context default.
    /// </summary>
    public EfInterceptorsSetup WithCommandTimeout(Func<string, int?> timeoutSelector)
        => Add(new CommandTimeoutCommandInterceptor(timeoutSelector));

    /// <summary>Per-command timeout keyed by TagWith tags (seconds).</summary>
    public EfInterceptorsSetup WithCommandTimeoutByTags(IReadOnlyDictionary<string, int> timeoutsByTag)
        => Add(new CommandTimeoutCommandInterceptor(
            CommandTimeoutCommandInterceptor.FromTags(timeoutsByTag)));

    /// <summary>
    /// Blocks commands by CommandSource. Default: forbid running EF migrations from application
    /// runtime (migrations belong to CI/CD).
    /// </summary>
    public EfInterceptorsSetup WithCommandSourceBlocker(params CommandSource[] blockedSources)
        => Add(new CommandSourceBlocker(blockedSources));

    /// <summary>Warns (and optionally calls back) whenever FromSqlRaw/SqlQuery/ExecuteSqlRaw is used.</summary>
    public EfInterceptorsSetup WithRawSqlUsageDetection(
        ILoggerFactory? loggerFactory = null,
        Action<CommandSource, string>? onRawSqlExecuted = null)
        => Add(new RawSqlUsageDetector(loggerFactory, onRawSqlExecuted));

    /// <summary>Guards ExecuteUpdate/ExecuteDelete that bypass SaveChanges interceptors (soft-delete, encryption, audit).</summary>
    public EfInterceptorsSetup WithBulkOperationGuard(BulkOperationPolicy policy = BulkOperationPolicy.Throw, ILoggerFactory? loggerFactory = null)
        => Add(new BulkOperationGuardInterceptor(policy, loggerFactory));

    /// <summary>Guards ExecuteUpdate/ExecuteDelete with explicit guarded table set.</summary>
    public EfInterceptorsSetup WithBulkOperationGuard(BulkOperationPolicy policy, IReadOnlySet<string> guardedTables, ILoggerFactory? loggerFactory = null)
        => Add(new BulkOperationGuardInterceptor(policy, guardedTables, loggerFactory));

    /// <summary>Requires the exact set of TagWith tags on every query.</summary>
    public EfInterceptorsSetup WithRequiredQueryTags(params string[] requiredTags)
        => Add(new RequireQueryTagsInterceptor(requiredTags));

    /// <summary>Rejects completely untagged queries.</summary>
    public EfInterceptorsSetup WithRequireAnyQueryTag()
        => Add(new RequireQueryTagsInterceptor([], requireAtLeastOneTag: true));

    /// <summary>
    /// Rejects forbidden query shapes at compilation: IgnoreQueryFilters by default,
    /// optionally ExecuteDelete/ExecuteUpdate.
    /// </summary>
    public EfInterceptorsSetup WithStrictQueryPolicy(
        bool forbidIgnoreQueryFilters = true,
        bool forbidExecuteDelete = false,
        bool forbidExecuteUpdate = false)
        => Add(new StrictQueryPolicyQueryExpressionInterceptor(
            forbidIgnoreQueryFilters, forbidExecuteDelete, forbidExecuteUpdate));

    /// <summary>Guards unbounded queries (no Take/First/Single).</summary>
    public EfInterceptorsSetup WithUnboundedQueryGuard(int maxRows = 0)
        => Add(new UnboundedQueryGuardInterceptor(maxRows));

    /// <summary>Resilience retries for transient command failures.</summary>
    public EfInterceptorsSetup WithResilience(int maxRetries = 2, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null, ILoggerFactory? loggerFactory = null)
        => Add(new ResilienceCommandInterceptor(maxRetries, baseDelay, maxDelay, loggerFactory));

    /// <summary>
    /// Query row budget (03.6): throws <c>QueryBudgetExceededException</c> once a single
    /// result set exceeds <paramref name="maxRows"/> rows. Scope with <paramref name="scopeFilter"/>
    /// (e.g. only <c>TagWith("web-request")</c> queries) so reports keep their own budget.
    /// </summary>
    public EfInterceptorsSetup WithQueryBudget(int maxRows, Func<Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData, bool>? scopeFilter = null)
        => Add(new QueryBudgetCommandInterceptor(maxRows, scopeFilter));
}
