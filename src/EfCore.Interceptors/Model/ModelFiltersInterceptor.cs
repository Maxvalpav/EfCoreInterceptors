using System.Linq.Expressions;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Model;

/// <summary>
/// Helper that applies soft-delete and tenant filters.
/// Use modelBuilder.ApplySoftDeleteFilters() / ApplyTenantFilters(provider) directly,
/// or via WithModelFilters() which registers this interceptor for diagnostic purposes.
/// Auto-application via IModelFinalizingInterceptor is available on EF Core 8+ when supported.
/// </summary>
public class ModelFiltersInterceptor : IInterceptor
{
    private readonly ITenantProvider? _tenantProvider;
    private readonly bool _enableSoftDelete;
    private readonly bool _enableTenant;

    public ModelFiltersInterceptor(ITenantProvider? tenantProvider = null, bool enableSoftDelete = true, bool enableTenant = true)
    {
        _tenantProvider = tenantProvider;
        _enableSoftDelete = enableSoftDelete;
        _enableTenant = enableTenant;
    }

    /// <summary>Applies filters to the given ModelBuilder (call from OnModelCreating).</summary>
    public void ApplyTo(ModelBuilder modelBuilder)
    {
        if (_enableSoftDelete) modelBuilder.ApplySoftDeleteFilters();
        if (_enableTenant && _tenantProvider is not null) modelBuilder.ApplyTenantFilters(_tenantProvider);
    }

    // NOTE: EF Core 10 does not expose a stable IModelFinalizingInterceptor for global filters.
    // Filters must be applied via ApplyTo(modelBuilder) in OnModelCreating. This interceptor is kept
    // for DI discovery via WithModelFilters() and to avoid dead-code warnings.
}
