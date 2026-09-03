using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Connections;
using EfCore.Interceptors.Materialization;
using EfCore.Interceptors.Observability;
using EfCore.Interceptors.Queries;
using EfCore.Interceptors.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors;

// Grouped observability / connection / materialization builders (09.1).
public sealed partial class EfInterceptorsSetup
{
    /// <summary>Publishes ef.save.duration/executed/failed/entities metrics.</summary>
    public EfInterceptorsSetup WithSaveChangesMetrics()
        => Add(new SaveChangesMetricsInterceptor());

    /// <summary>Publishes ef.transaction.started/committed/rolledback/failed/duration metrics.</summary>
    public EfInterceptorsSetup WithTransactionMetrics()
        => Add(new TransactionMetricsInterceptor());

    /// <summary>Publishes ef.connection.opened/closed/failed/open_duration metrics.</summary>
    public EfInterceptorsSetup WithConnectionMetrics()
        => Add(new ConnectionMetricsInterceptor());

    /// <summary>Warns when a transaction stays open longer than the threshold.</summary>
    public EfInterceptorsSetup WithLongRunningTransactionDetection(
        TimeSpan threshold, ILoggerFactory? loggerFactory = null)
        => Add(new LongRunningTransactionDetector(threshold, loggerFactory));

    /// <summary>Предупреждает, когда один SaveChanges порождает больше N команд (скрытый N+1 на записи).</summary>
    public EfInterceptorsSetup WithCommandsPerSaveDiagnostics(int warnAbove = 10, ILoggerFactory? loggerFactory = null)
        => Add(new CommandsPerSaveDiagnosticInterceptor(warnAbove, loggerFactory));

    /// <summary>Warns when a single SaveChanges exceeds the threshold.</summary>
    public EfInterceptorsSetup WithSlowSaves(TimeSpan threshold, ILoggerFactory? loggerFactory = null)
        => Add(new SlowSaveChangesDetector(threshold, loggerFactory));

    /// <summary>OpenTelemetry ActivitySource tracing for SaveChanges/Commands.</summary>
    public EfInterceptorsSetup WithTracing()
        => Add(new TracingSaveChangesInterceptor()).Add(new TracingCommandInterceptor());

    /// <summary>Publishes ef.materialization.entities counter (spike = cartesian explosion).</summary>
    public EfInterceptorsSetup WithMaterializationMetrics()
        => Add(new MaterializationMetricsInterceptor());

    /// <summary>Stamps ILoadTimestamped.LoadedAtUtc when entities are materialized.</summary>
    public EfInterceptorsSetup WithLoadStamping(TimeProvider? clock = null)
        => Add(new LoadStampingMaterializationInterceptor(clock));

    /// <summary>Calls IInitializable.OnLoaded after entity materialization.</summary>
    public EfInterceptorsSetup WithInitialization()
        => Add(new InitializationMaterializationInterceptor());

    /// <summary>DLP masking on materialization for [Masked] properties.</summary>
    public EfInterceptorsSetup WithMasking(IMaskingPolicy? policy = null)
        => Add(new MaskingMaterializationInterceptor(policy));

    /// <summary>Dumps LINQ expression trees at Debug level before query compilation.</summary>
    public EfInterceptorsSetup WithQueryTreeLogging(ILoggerFactory? loggerFactory = null)
        => Add(new QueryTreeLoggingInterceptor(loggerFactory));

    /// <summary>Logs connection open/close/fail events with masked secrets.</summary>
    public EfInterceptorsSetup WithConnectionLogging(ILoggerFactory? loggerFactory = null)
        => Add(new ConnectionLifecycleLoggingInterceptor(loggerFactory));

    /// <summary>Logs transaction begin/commit/rollback/savepoints.</summary>
    public EfInterceptorsSetup WithTransactionLogging(ILoggerFactory? loggerFactory = null)
        => Add(new TransactionLifecycleLoggingInterceptor(loggerFactory));

    /// <summary>Runs session-scoped SQL statements whenever a connection opens.</summary>
    public EfInterceptorsSetup WithSessionInit(IEnumerable<string> statements, ILoggerFactory? loggerFactory = null)
        => Add(new SessionInitConnectionInterceptor(statements, loggerFactory));

    /// <summary>Session-init statements resolved per open (e.g. tenant id into session context).</summary>
    public EfInterceptorsSetup WithSessionInit(
        Func<DbContext?, IEnumerable<string>> statementResolver,
        ILoggerFactory? loggerFactory = null)
        => Add(new SessionInitConnectionInterceptor(statementResolver, loggerFactory));

    /// <summary>Resolves the connection string at open time (database-per-tenant / routing).</summary>
    public EfInterceptorsSetup WithDynamicConnectionString(Func<DbContext?, string> resolver, ILoggerFactory? loggerFactory = null)
        => Add(new DynamicConnectionStringConnectionInterceptor(resolver, loggerFactory));

    /// <summary>Forces every transaction (explicit and implicit) onto the given isolation level.</summary>
    public EfInterceptorsSetup WithForcedIsolationLevel(System.Data.IsolationLevel isolationLevel)
        => Add(new ForcedIsolationLevelTransactionInterceptor(isolationLevel));
}
