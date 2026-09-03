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

    private void AddOrReplace<T>(T interceptor) where T : IInterceptor
    {
        _interceptors.RemoveAll(i => i.GetType() == typeof(T));
        _interceptors.Add(interceptor);
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
        }
        if (_interceptors.Count > 0)
        {
            var hasCommandInterceptor = _interceptors.Any(i => i is DbCommandInterceptor);
            if (hasCommandInterceptor)
            {
                var extensionNames = string.Join(",", builder.Options.Extensions.Select(e => e.GetType().Name));
                if (extensionNames.Contains("Cosmos", StringComparison.OrdinalIgnoreCase) || extensionNames.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
                {
                    var logger = _loggerFactory?.CreateLogger("EfCore.Interceptors");
                    var msg = $"[EfCore.Interceptors] Warning: command interceptors registered with non-relational provider ({extensionNames}) — they will never be invoked.";
                    if (logger != null) logger.LogWarning("{Message}", msg);
                    else System.Diagnostics.Trace.WriteLine(msg);
                }
            }
            var ordered = _interceptors
                .OrderBy(i => (i as IOrderedInterceptor)?.Order ?? 0)
                .ToArray();
            builder.AddInterceptors(ordered);
        }
    }

    /// <summary>Adds a custom interceptor.</summary>
    public EfInterceptorsSetup Add(IInterceptor interceptor)
    {
        _interceptors.Add(interceptor);
        return this;
    }

    /// <summary>
    /// Resolves identity conflicts: incoming values overwrite tracked state (true)
    /// or tracked state wins (false). Requires ChangeTracker.IdentityResolutionBehavior
    /// set to UpdateTracked.
    /// </summary>
    [Obsolete("Boolean trap (02.7): use WithIdentityResolution(IdentityResolutionMode) instead.")]
    public EfInterceptorsSetup WithIdentityResolution(bool overwriteExisting)
        => Add(overwriteExisting
            ? new OverwriteIdentityResolutionInterceptor()
            : new IgnoreIncomingIdentityResolutionInterceptor());

    /// <summary>Resolves identity conflicts via explicit mode (avoids boolean trap).</summary>
    public EfInterceptorsSetup WithIdentityResolution(Tracking.IdentityResolutionMode mode)
        => mode == Tracking.IdentityResolutionMode.Overwrite
            ? Add(new OverwriteIdentityResolutionInterceptor())
            : Add(new IgnoreIncomingIdentityResolutionInterceptor());

    /// <summary>
    /// Identity resolution that fills only null/empty properties of the tracked instance —
    /// non-empty tracked data always wins.
    /// </summary>
    public EfInterceptorsSetup WithNullMergingIdentityResolution()
        => Add(new Tracking.NullMergeIdentityResolutionInterceptor());

    /// <summary>Identity resolution where the instance with the newer UpdatedAtUtc survives.</summary>
    public EfInterceptorsSetup WithNewestWinsIdentityResolution()
        => Add(new Tracking.NewestWinsIdentityResolutionInterceptor());

    /// <summary>Auto-applies soft-delete and tenant filters via model finalizer.</summary>
    public EfInterceptorsSetup WithModelFilters(Abstractions.ITenantProvider? tenantProvider = null, bool softDelete = true, bool tenant = true)
        => Add(new Model.ModelFiltersInterceptor(tenantProvider, softDelete, tenant));

    /// <summary>
    /// Stamps TenantId on inserts and rejects cross-tenant modifications.
    /// Also auto-registers <c>TenantModelCacheKeyFactory</c> so the model cache
    /// varies per tenant (02.6) — no manual <c>ReplaceService</c> needed.
    /// </summary>
    public EfInterceptorsSetup WithMultiTenancy(Abstractions.ITenantProvider tenantProvider)
    {
        _tenantProvider = tenantProvider;
        return Add(new Saving.MultiTenancySaveChangesInterceptor(tenantProvider));
    }
}
