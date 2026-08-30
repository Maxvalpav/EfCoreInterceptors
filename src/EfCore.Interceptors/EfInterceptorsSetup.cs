using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Connections;
using EfCore.Interceptors.Materialization;
using EfCore.Interceptors.Queries;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tracking;
using EfCore.Interceptors.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors;

/// <summary>
/// Fluent configurator that assembles the interceptor set.
/// Obtain it through <see cref="DbContextOptionsBuilderExtensions.UseEfInterceptors"/>
/// or <see cref="ServiceCollectionExtensions.AddEfInterceptors"/>.
/// </summary>
public sealed class EfInterceptorsSetup
{
    private readonly List<IInterceptor> _interceptors = [];

    internal IReadOnlyList<IInterceptor> Interceptors => _interceptors;

    internal void BuildInto(DbContextOptionsBuilder builder)
    {
        if (_interceptors.Count > 0)
        {
            builder.AddInterceptors([.. _interceptors]);
        }
    }

    /// <summary>Adds a custom interceptor.</summary>
    public EfInterceptorsSetup Add(IInterceptor interceptor)
    {
        _interceptors.Add(interceptor);
        return this;
    }

    /// <summary>Fills Created/Updated audit columns on IAuditableEntity entities.</summary>
    public EfInterceptorsSetup WithAuditing(
        ICurrentUserProvider? currentUserProvider = null,
        TimeProvider? clock = null)
        => Add(new AuditSaveChangesInterceptor(currentUserProvider, clock));

    /// <summary>Turns deletes of ISoftDeletableEntity entities into logical deletes.</summary>
    public EfInterceptorsSetup WithSoftDeletes(
        ICurrentUserProvider? currentUserProvider = null,
        TimeProvider? clock = null)
        => Add(new SoftDeleteSaveChangesInterceptor(currentUserProvider, clock));

    /// <summary>Publishes domain events after successful saves.</summary>
    public EfInterceptorsSetup WithDomainEvents(IDomainEventDispatcher? dispatcher = null)
        => Add(new DomainEventsSaveChangesInterceptor(dispatcher));

    /// <summary>Warns when commands exceed the configured duration threshold.</summary>
    public EfInterceptorsSetup WithSlowQueryWarning(
        TimeSpan threshold,
        ILoggerFactory? loggerFactory = null,
        Func<Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData, bool>? filter = null)
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

    /// <summary>Throws before any write statement reaches the database.</summary>
    public EfInterceptorsSetup WithReadOnlyGuard(Func<DbContext?, bool>? isEnabled = null)
        => Add(new ReadOnlyGuardCommandInterceptor(isEnabled));

    /// <summary>Runs session-scoped SQL statements whenever a connection opens.</summary>
    public EfInterceptorsSetup WithSessionInit(IEnumerable<string> statements, ILoggerFactory? loggerFactory = null)
        => Add(new SessionInitConnectionInterceptor(statements, loggerFactory));

    /// <summary>Logs connection open/close/fail events with masked secrets.</summary>
    public EfInterceptorsSetup WithConnectionLogging(ILoggerFactory? loggerFactory = null)
        => Add(new ConnectionLifecycleLoggingInterceptor(loggerFactory));

    /// <summary>Logs transaction begin/commit/rollback/savepoints.</summary>
    public EfInterceptorsSetup WithTransactionLogging(ILoggerFactory? loggerFactory = null)
        => Add(new TransactionLifecycleLoggingInterceptor(loggerFactory));

    /// <summary>Dumps LINQ expression trees at Debug level before query compilation.</summary>
    public EfInterceptorsSetup WithQueryTreeLogging(ILoggerFactory? loggerFactory = null)
        => Add(new QueryTreeLoggingInterceptor(loggerFactory));

    /// <summary>Stamps ILoadTimestamped.LoadedAtUtc when entities are materialized.</summary>
    public EfInterceptorsSetup WithLoadStamping(TimeProvider? clock = null)
        => Add(new LoadStampingMaterializationInterceptor(clock));

    /// <summary>
    /// Resolves identity conflicts: incoming values overwrite tracked state (true)
    /// or tracked state wins (false). Requires ChangeTracker.IdentityResolutionBehavior
    /// set to UpdateTracked.
    /// </summary>
    public EfInterceptorsSetup WithIdentityResolution(bool overwriteExisting)
        => Add(overwriteExisting
            ? new OverwriteIdentityResolutionInterceptor()
            : new IgnoreIncomingIdentityResolutionInterceptor());

    // ---------- Wave 2: advanced scenarios ----------

    /// <summary>
    /// Writes a per-property audit trail (old/new JSON diff) to the mapped ChangeLogEntries table
    /// in the same transaction. Requires modelBuilder.Entity&lt;ChangeLogEntry&gt;().
    /// </summary>
    public EfInterceptorsSetup WithChangeLog(
        ICurrentUserProvider? currentUserProvider = null,
        TimeProvider? clock = null)
        => Add(new Saving.ChangeLogSaveChangesInterceptor(currentUserProvider, clock));

    /// <summary>
    /// Atomic outbox: serializes domain events into the mapped OutboxMessages table inside the same
    /// transaction as the business change. Requires modelBuilder.Entity&lt;OutboxMessage&gt;().
    /// </summary>
    public EfInterceptorsSetup WithOutbox()
        => Add(new Saving.OutboxSaveChangesInterceptor());

    /// <summary>Stamps TenantId on inserts and rejects cross-tenant modifications.</summary>
    public EfInterceptorsSetup WithMultiTenancy(Abstractions.ITenantProvider tenantProvider)
        => Add(new Saving.MultiTenancySaveChangesInterceptor(tenantProvider));

    /// <summary>Aborts SaveChanges that would touch more rows than the configured limits.</summary>
    public EfInterceptorsSetup WithMassOperationGuard(int maxAdded = 100, int maxModified = 100, int maxDeleted = 100)
        => Add(new Saving.MassOperationGuardSaveChangesInterceptor(maxAdded, maxModified, maxDeleted));

    /// <summary>Translates DbUpdateConcurrencyException into ConcurrencyConflictException.</summary>
    public EfInterceptorsSetup WithConcurrencyTranslation()
        => Add(new Saving.ConcurrencyExceptionTranslatorInterceptor());

    /// <summary>
    /// Read-through in-memory query cache (same SQL + parameters served without a DB roundtrip).
    /// Writes invalidate everything; entries expire after timeToLive.
    /// </summary>
    public EfInterceptorsSetup WithSecondLevelCache(TimeSpan? timeToLive = null, bool skipInsideTransactions = true)
        => Add(new Commands.CachingCommandInterceptor(timeToLive, skipInsideTransactions));

    /// <summary>Warns when one context repeats the identical SQL template N times (N+1 signature).</summary>
    public EfInterceptorsSetup WithNPlusOneDetection(int threshold = 5, ILoggerFactory? loggerFactory = null)
        => Add(new Commands.NPlusOneDetectorCommandInterceptor(threshold, loggerFactory));

    /// <summary>Publishes ef.command.duration/executed/failed metrics via System.Diagnostics.Metrics.</summary>
    public EfInterceptorsSetup WithCommandMetrics(string? meterName = null)
        => Add(new Commands.MetricsCommandInterceptor(meterName));

    /// <summary>Rejects write statements executed outside an explicit transaction.</summary>
    public EfInterceptorsSetup WithTransactionalWrites(Func<DbContext?, bool>? isEnabled = null)
        => Add(new Commands.WritesRequireTransactionCommandInterceptor(isEnabled));

    /// <summary>Resolves the connection string at open time (database-per-tenant / routing).</summary>
    public EfInterceptorsSetup WithDynamicConnectionString(Func<DbContext?, string> resolver, ILoggerFactory? loggerFactory = null)
        => Add(new Connections.DynamicConnectionStringConnectionInterceptor(resolver, loggerFactory));

    /// <summary>Forces every transaction (explicit and implicit) onto the given isolation level.</summary>
    public EfInterceptorsSetup WithForcedIsolationLevel(System.Data.IsolationLevel isolationLevel)
        => Add(new Transactions.ForcedIsolationLevelTransactionInterceptor(isolationLevel));

    /// <summary>
    /// Rejects forbidden query shapes at compilation: IgnoreQueryFilters by default,
    /// optionally ExecuteDelete/ExecuteUpdate.
    /// </summary>
    public EfInterceptorsSetup WithStrictQueryPolicy(
        bool forbidIgnoreQueryFilters = true,
        bool forbidExecuteDelete = false,
        bool forbidExecuteUpdate = false)
        => Add(new Queries.StrictQueryPolicyQueryExpressionInterceptor(
            forbidIgnoreQueryFilters, forbidExecuteDelete, forbidExecuteUpdate));

    /// <summary>
    /// Identity resolution that fills only null/empty properties of the tracked instance —
    /// non-empty tracked data always wins.
    /// </summary>
    public EfInterceptorsSetup WithNullMergingIdentityResolution()
        => Add(new Tracking.NullMergeIdentityResolutionInterceptor());

    // ---------- Wave 3: guards, observability, encryption ----------

    /// <summary>Rejects deletes of IProtectedEntity entities with ProtectedEntityException.</summary>
    public EfInterceptorsSetup WithDeleteGuard()
        => Add(new Saving.DeleteGuardSaveChangesInterceptor());

    /// <summary>Rejects modifications/deletes of IImmutableEntity (append-only) entities.</summary>
    public EfInterceptorsSetup WithImmutableGuard()
        => Add(new Saving.ImmutableEntityGuardSaveChangesInterceptor());

    /// <summary>Publishes ef.save.duration/executed/failed/entities metrics.</summary>
    public EfInterceptorsSetup WithSaveChangesMetrics()
        => Add(new Observability.SaveChangesMetricsInterceptor());

    /// <summary>Publishes ef.transaction.started/committed/rolledback/failed/duration metrics.</summary>
    public EfInterceptorsSetup WithTransactionMetrics()
        => Add(new Observability.TransactionMetricsInterceptor());

    /// <summary>Publishes ef.connection.opened/closed/failed/open_duration metrics.</summary>
    public EfInterceptorsSetup WithConnectionMetrics()
        => Add(new Observability.ConnectionMetricsInterceptor());

    /// <summary>Warns when a transaction stays open longer than the threshold.</summary>
    public EfInterceptorsSetup WithLongRunningTransactionDetection(
        TimeSpan threshold, ILoggerFactory? loggerFactory = null)
        => Add(new Observability.LongRunningTransactionDetector(threshold, loggerFactory));

    /// <summary>
    /// Per-command timeout from a selector over the SQL text — e.g. give TagWith("report")
    /// queries 300s while everything else keeps the context default.
    /// </summary>
    public EfInterceptorsSetup WithCommandTimeout(Func<string, int?> timeoutSelector)
        => Add(new Commands.CommandTimeoutCommandInterceptor(timeoutSelector));

    /// <summary>Per-command timeout keyed by TagWith tags (seconds).</summary>
    public EfInterceptorsSetup WithCommandTimeoutByTags(IReadOnlyDictionary<string, int> timeoutsByTag)
        => Add(new Commands.CommandTimeoutCommandInterceptor(
            Commands.CommandTimeoutCommandInterceptor.FromTags(timeoutsByTag)));

    /// <summary>Calls IInitializable.OnLoaded after entity materialization.</summary>
    public EfInterceptorsSetup WithInitialization()
        => Add(new Materialization.InitializationMaterializationInterceptor());

    /// <summary>
    /// Transparent property encryption: [Encrypted] string properties are encrypted on save and
    /// decrypted on materialization. Provide a real IPropertyValueEncryptor in production.
    /// </summary>
    public EfInterceptorsSetup WithPropertyEncryption(Abstractions.IPropertyValueEncryptor encryptor)
        => Add(new Saving.PropertyEncryptionSaveChangesInterceptor(encryptor))
           .Add(new Materialization.PropertyDecryptionMaterializationInterceptor(encryptor));

    // ---------- Wave 4: validation, redaction, factories ----------

    /// <summary>
    /// Runs DataAnnotations validation over every Added/Modified entity before the save and
    /// aborts with EntityValidationException listing ALL violations at once.
    /// </summary>
    public EfInterceptorsSetup WithValidation()
        => Add(new Saving.ValidationSaveChangesInterceptor());

    /// <summary>Warns when a single SaveChanges exceeds the threshold.</summary>
    public EfInterceptorsSetup WithSlowSaves(TimeSpan threshold, ILoggerFactory? loggerFactory = null)
        => Add(new Observability.SlowSaveChangesDetector(threshold, loggerFactory));

    /// <summary>
    /// Logs every SQL command with duration. Options: include parameter values, redact sensitive
    /// fragments (PII/cards) and sample non-error logs (failures are always logged).
    /// Replaces any previously added SQL logger.
    /// </summary>
    public EfInterceptorsSetup WithSqlLogging(
        ILoggerFactory? loggerFactory = null,
        bool includeParameterValues = false,
        Func<string, string>? textRedactor = null,
        double sampleRate = 1.0)
    {
        _interceptors.RemoveAll(i => i is Commands.SqlLoggingCommandInterceptor);
        return Add(new Commands.SqlLoggingCommandInterceptor(loggerFactory, includeParameterValues, textRedactor, sampleRate));
    }

    /// <summary>
    /// Registers factory methods for entity types without a parameterless constructor;
    /// EF will construct those instances through the factories and bind column values on top.
    /// </summary>
    public EfInterceptorsSetup WithConstructorFactories(IReadOnlyDictionary<Type, Func<object>> factoriesByType)
        => Add(new Materialization.FactoryMethodInstantiationBindingInterceptor(factoriesByType));

    // ---------- Wave 5: governance и тонкая настройка ----------

    /// <summary>
    /// Blocks commands by CommandSource. Default: forbid running EF migrations from application
    /// runtime (migrations belong to CI/CD).
    /// </summary>
    public EfInterceptorsSetup WithCommandSourceBlocker(params Microsoft.EntityFrameworkCore.Diagnostics.CommandSource[] blockedSources)
        => Add(new Commands.CommandSourceBlocker(blockedSources));

    /// <summary>Warns (and optionally calls back) whenever FromSqlRaw/SqlQuery/ExecuteSqlRaw is used.</summary>
    public EfInterceptorsSetup WithRawSqlUsageDetection(
        ILoggerFactory? loggerFactory = null,
        Action<Microsoft.EntityFrameworkCore.Diagnostics.CommandSource, string>? onRawSqlExecuted = null)
        => Add(new Commands.RawSqlUsageDetector(loggerFactory, onRawSqlExecuted));

    /// <summary>Maintains IVersionedEntity.Version (+1 on update) for optimistic concurrency.</summary>
    public EfInterceptorsSetup WithVersionCounter()
        => Add(new Saving.VersionIncrementSaveChangesInterceptor());

    /// <summary>Identity resolution where the instance with the newer UpdatedAtUtc survives.</summary>
    public EfInterceptorsSetup WithNewestWinsIdentityResolution()
        => Add(new Tracking.NewestWinsIdentityResolutionInterceptor());

    /// <summary>Publishes ef.materialization.entities counter (spike = cartesian explosion).</summary>
    public EfInterceptorsSetup WithMaterializationMetrics()
        => Add(new Observability.MaterializationMetricsInterceptor());

    /// <summary>Requires the exact set of TagWith tags on every query.</summary>
    public EfInterceptorsSetup WithRequiredQueryTags(params string[] requiredTags)
        => Add(new Queries.RequireQueryTagsInterceptor(requiredTags));

    /// <summary>Rejects completely untagged queries.</summary>
    public EfInterceptorsSetup WithRequireAnyQueryTag()
        => Add(new Queries.RequireQueryTagsInterceptor([], requireAtLeastOneTag: true));

    /// <summary>Session-init statements resolved per open (e.g. tenant id into session context).</summary>
    public EfInterceptorsSetup WithSessionInit(
        Func<DbContext?, IEnumerable<string>> statementResolver,
        ILoggerFactory? loggerFactory = null)
        => Add(new Connections.SessionInitConnectionInterceptor(statementResolver, loggerFactory));

    // ---------- Wave 6: приёмы как интерсепторы ----------

    /// <summary>
    /// Классический «retry вокруг SaveChanges» при оптимистичной конкуренции:
    /// конфликт разрешается по политике (ClientWins/StoreWins) и сохранение повторяется
    /// до maxRetries раз с экспоненциальной задержкой. Нужен concurrency-токен в модели.
    /// </summary>
    public EfInterceptorsSetup WithConcurrencyRetry(
        Saving.ConcurrencyRetryPolicy policy = Saving.ConcurrencyRetryPolicy.ClientWins,
        int maxRetries = 3,
        TimeSpan? initialDelay = null)
        => Add(new Saving.ConcurrencyRetrySaveChangesInterceptor(policy, maxRetries, initialDelay));

    /// <summary>
    /// Точка адаптации внешних валидаторов (FluentValidation и т.п.) без зависимости от них:
    /// реализуйте IEntityValidator и передайте экземпляры сюда.
    /// </summary>
    public EfInterceptorsSetup WithCustomValidation(params Abstractions.IEntityValidator[] validators)
        => Add(new Saving.CustomValidationSaveChangesInterceptor(validators));

    /// <summary>Предупреждает, когда один SaveChanges порождает больше N команд (скрытый N+1 на записи).</summary>
    public EfInterceptorsSetup WithCommandsPerSaveDiagnostics(int warnAbove = 10, ILoggerFactory? loggerFactory = null)
        => Add(new Observability.CommandsPerSaveDiagnosticInterceptor(warnAbove, loggerFactory));

    /// <summary>Trims all string properties (Added/Modified) before save.</summary>
    public EfInterceptorsSetup WithStringTrimming(Func<string, string?>? normalize = null)
        => Add(new Saving.StringTrimmingSaveChangesInterceptor(normalize));

    /// <summary>Shadow-property auditing without IAuditableEntity.</summary>
    public EfInterceptorsSetup WithShadowAuditing(Abstractions.ICurrentUserProvider? currentUserProvider = null, TimeProvider? clock = null)
        => Add(new Saving.ShadowAuditSaveChangesInterceptor(currentUserProvider, clock));

    /// <summary>Auto-applies soft-delete and tenant filters via model finalizer.</summary>
    public EfInterceptorsSetup WithModelFilters(Abstractions.ITenantProvider? tenantProvider = null, bool softDelete = true, bool tenant = true)
        => Add(new Model.ModelFiltersInterceptor(tenantProvider, softDelete, tenant));
    /// <summary>OpenTelemetry ActivitySource tracing for SaveChanges/Commands.</summary>
    public EfInterceptorsSetup WithTracing()
        => Add(new Observability.TracingSaveChangesInterceptor()).Add(new Observability.TracingCommandInterceptor());

    /// <summary>Guards unbounded queries (no Take/First/Single).</summary>
    public EfInterceptorsSetup WithUnboundedQueryGuard(int maxRows = 0)
        => Add(new Queries.UnboundedQueryGuardInterceptor(maxRows));

    /// <summary>DLP masking on materialization for [Masked] properties.</summary>
    public EfInterceptorsSetup WithMasking(Abstractions.IMaskingPolicy? policy = null)
        => Add(new Materialization.MaskingMaterializationInterceptor(policy));

    /// <summary>PostgreSQL FOR UPDATE/SHARE locking hints via TagWith.</summary>
    public EfInterceptorsSetup WithPostgresHints(IReadOnlyDictionary<string, string>? hintsByTag = null)
        => Add(new Commands.QueryHintsCommandInterceptor(hintsByTag: hintsByTag ?? Queries.PostgresQueryHints.Defaults));

    /// <summary>Deterministic searchable encryption for equality lookups.</summary>
    public EfInterceptorsSetup WithSearchableEncryption(Abstractions.IPropertyValueEncryptor encryptor)
        => Add(new Saving.PropertyEncryptionSaveChangesInterceptor(encryptor))
           .Add(new Materialization.PropertyDecryptionMaterializationInterceptor(encryptor));

    /// <summary>Resilience retries for transient command failures.</summary>
    public EfInterceptorsSetup WithResilience(int maxRetries = 2, TimeSpan? baseDelay = null, ILoggerFactory? loggerFactory = null)
        => Add(new Commands.ResilienceCommandInterceptor(maxRetries, baseDelay, loggerFactory));

    /// <summary>HybridCache second-level cache (shared IMemoryCache).</summary>
    public EfInterceptorsSetup WithHybridCache(Microsoft.Extensions.Caching.Memory.IMemoryCache cache, TimeSpan? ttl = null, bool skipInsideTransactions = true)
        => Add(new Commands.HybridCacheCommandInterceptor(cache, ttl, skipInsideTransactions));

}

