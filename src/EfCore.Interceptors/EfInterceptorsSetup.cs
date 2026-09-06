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
/// Grouped methods live in partials: Saving / Commands / Observability (09.1).
/// </summary>
public static class InterceptorOrder
{
    public const int Validation = -300;
    public const int Guards = -200;
    public const int MultiTenancy = -150;
    public const int SoftDelete = -100;
    public const int Audit = 0;
    public const int Version = 50;
    public const int Encryption = 75;
    public const int ChangeLog = 100;
    public const int Outbox = 200;
    public const int DomainEvents = 300;
    public const int Metrics = 1000;
}

public sealed partial class EfInterceptorsSetup
{
    private readonly List<IInterceptor> _interceptors = [];
    private ILoggerFactory? _loggerFactory;
    private Abstractions.ITenantProvider? _tenantProvider;

    internal IReadOnlyList<IInterceptor> Interceptors => _interceptors;

    public EfInterceptorsSetup WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        return this;
    }

    private EfInterceptorsSetup AddOrReplace(IInterceptor interceptor)
    {
        var type = interceptor.GetType();
        _interceptors.RemoveAll(i => i.GetType() == type);
        _interceptors.Add(interceptor);
        return this;
    }

    internal void BuildInto(DbContextOptionsBuilder builder)
    {
        // Auto-register the tenant model-cache-key factory (02.6): without it the first
        // tenant's model sticks for every tenant. Explicit ReplaceService AFTER
        // UseEfInterceptors still wins (EF applies registrations in order).
        if (_tenantProvider is not null)
        {
            builder.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, Model.TenantModelCacheKeyFactory>();
            _loggerFactory?.CreateLogger("EfCore.Interceptors").LogInformation(
                "TenantModelCacheKeyFactory auto-registered (WithMultiTenancy).");
            // Tenant-isolate every registered second-level cache: constant-captured filters,
            // per-tenant models and session/schema isolation can otherwise serve tenant A's
            // rows to tenant B from the cache (identical SQL + parameters, one connection string).
            foreach (var c in _interceptors.OfType<Commands.CachingCommandInterceptor>())
                c.AddContributor(new Abstractions.TenantKeyContributor(_tenantProvider));
        }
        if (_interceptors.Count > 0)
        {
            var hasCommandInterceptor = _interceptors.Any(i => i is DbCommandInterceptor);
            if (hasCommandInterceptor)
            {
                var isRelational = builder.Options.Extensions.OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>().Any();
                if (!isRelational)
                {
                    var extensionNames = string.Join(",", builder.Options.Extensions.Select(e => e.GetType().Name));
                    var logger = _loggerFactory?.CreateLogger("EfCore.Interceptors");
                    var msg = $"[EfCore.Interceptors] Warning: command interceptors registered with non-relational provider ({extensionNames}) — they will never be invoked.";
                    if (logger != null) logger.LogWarning("{Message}", msg);
                    else System.Diagnostics.Trace.WriteLine(msg);
                }
            }
            if (_interceptors.OfType<Commands.CachingCommandInterceptor>().Any()
                && _interceptors.OfType<Connections.SessionInitConnectionInterceptor>().Any())
            {
                var logger = _loggerFactory?.CreateLogger("EfCore.Interceptors");
                var msg = "[EfCore.Interceptors] Warning: second-level cache is combined with session-init statements — session state (search_path, session context, RLS) is invisible to the cache key. Register an IQueryCacheKeyContributor covering the session state or exclude session-dependent queries from caching.";
                if (logger != null) logger.LogWarning("{Message}", msg);
                else System.Diagnostics.Trace.WriteLine(msg);
            }
            var ordered = _interceptors
                .OrderBy(i => (i as IOrderedInterceptor)?.Order ?? 0)
                .ToArray();
            builder.AddInterceptors(ordered);
        }
    }

    /// <summary>Adds a custom interceptor. Same reference twice is ignored.</summary>
    public EfInterceptorsSetup Add(IInterceptor interceptor)
    {
        if (!_interceptors.Any(i => ReferenceEquals(i, interceptor)))
            _interceptors.Add(interceptor);
        return this;
    }

    /// <summary>
    /// Safe production starting point (audit §4.1/§4.6): SQL logging without parameter
    /// values, bulk guard (<c>Throw</c>), strict query policy (forbids
    /// <c>IgnoreQueryFilters/ExecuteDelete/ExecuteUpdate</c>), raw-SQL usage detection.
    /// Second-level cache stays off until an invalidation policy is chosen;
    /// query hints stay off until a provider-guarded set is registered.
    /// Reversible via <see cref="Commands.BulkOperationGuardInterceptor"/> policy or
    /// <c>AllowUnsafeBulkOperations</c> (explicit, logged).
    /// </summary>
    public EfInterceptorsSetup UseProductionDefaults(ILoggerFactory? loggerFactory = null)
    {
        if (loggerFactory is not null) WithLoggerFactory(loggerFactory);
        WithSqlLogging(loggerFactory, includeParameterValues: false);
        WithBulkOperationGuard(Commands.BulkOperationPolicy.Throw, loggerFactory);
        WithStrictQueryPolicy(
            forbidIgnoreQueryFilters: true,
            forbidExecuteDelete: true,
            forbidExecuteUpdate: true);
        WithRawSqlUsageDetection(loggerFactory);
        return this;
    }

    /// <summary>
    /// Resolves identity conflicts: incoming values overwrite tracked state (true)
    /// or tracked state wins (false). Requires ChangeTracker.IdentityResolutionBehavior
    /// set to UpdateTracked.
    /// </summary>
    [Obsolete("Boolean trap (02.7): use WithIdentityResolution(IdentityResolutionMode) instead.")]
    public EfInterceptorsSetup WithIdentityResolution(bool overwriteExisting)
        => AddOrReplace(overwriteExisting
            ? new OverwriteIdentityResolutionInterceptor()
            : new IgnoreIncomingIdentityResolutionInterceptor());

    /// <summary>Resolves identity conflicts via explicit mode (avoids boolean trap).</summary>
    public EfInterceptorsSetup WithIdentityResolution(Tracking.IdentityResolutionMode mode)
        => mode == Tracking.IdentityResolutionMode.Overwrite
            ? AddOrReplace(new OverwriteIdentityResolutionInterceptor())
            : AddOrReplace(new IgnoreIncomingIdentityResolutionInterceptor());

    /// <summary>
    /// Identity resolution that fills only null/empty properties of the tracked instance —
    /// non-empty tracked data always wins.
    /// </summary>
    public EfInterceptorsSetup WithNullMergingIdentityResolution()
        => AddOrReplace(new Tracking.NullMergeIdentityResolutionInterceptor());

    /// <summary>Identity resolution where the instance with the newer UpdatedAtUtc survives.</summary>
    public EfInterceptorsSetup WithNewestWinsIdentityResolution()
        => AddOrReplace(new Tracking.NewestWinsIdentityResolutionInterceptor());

    /// <summary>Auto-applies soft-delete and tenant filters via model finalizer.</summary>
    public EfInterceptorsSetup WithModelFilters(Abstractions.ITenantProvider? tenantProvider = null, bool softDelete = true, bool tenant = true)
        => AddOrReplace(new Model.ModelFiltersInterceptor(tenantProvider, softDelete, tenant));

    /// <summary>
    /// Stamps TenantId on inserts and rejects cross-tenant modifications.
    /// Also auto-registers <c>TenantModelCacheKeyFactory</c> so the model cache
    /// varies per tenant (02.6) — no manual <c>ReplaceService</c> needed.
    /// Tenant query filters are bypassable via <c>IgnoreQueryFilters()</c>/raw SQL, so a
    /// strict query policy (forbid <c>IgnoreQueryFilters</c>) is added by default
    /// (security S-2); pass <c>strictQueryPolicy: false</c> to opt out, e.g. for
    /// admin/recycle-bin contexts. Raw-SQL strictness stays opt-in — see
    /// <c>WithStrictQueryPolicy(forbidUntaggedRawSql: true)</c>.
    /// </summary>
    public EfInterceptorsSetup WithMultiTenancy(
        Abstractions.ITenantProvider tenantProvider,
        bool strictQueryPolicy = true)
    {
        _tenantProvider = tenantProvider;
        AddOrReplace(new Saving.MultiTenancySaveChangesInterceptor(tenantProvider));
        if (strictQueryPolicy && !_interceptors.OfType<Queries.StrictQueryPolicyQueryExpressionInterceptor>().Any())
        {
            AddOrReplace(new Queries.StrictQueryPolicyQueryExpressionInterceptor(forbidIgnoreQueryFilters: true));
        }
        return this;
    }
}
