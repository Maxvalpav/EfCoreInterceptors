using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Multi-tenancy enforcement:
/// on insert stamps ITenantEntity.TenantId with the current tenant; on any save rejects entities
/// whose stored TenantId differs from the current tenant (cross-tenant access guard).
/// Combine with a global query filter (<c>HasQueryFilter(e =&gt; e.TenantId == current)</c>)
/// or a query-expression policy for read isolation.
/// </summary>
public class MultiTenancySaveChangesInterceptor(ITenantProvider tenantProvider) : SaveChangesInterceptor
{
    private readonly ITenantProvider _tenants = tenantProvider;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ApplyTenantRules(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyTenantRules(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    protected virtual void ApplyTenantRules(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var currentTenant = _tenants.CurrentTenantId;

        foreach (var entry in context.ChangeTracker.Entries<ITenantEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.TenantId = currentTenant;
                    break;

                case EntityState.Modified when !string.Equals(entry.Entity.TenantId, currentTenant, StringComparison.Ordinal):
                    throw new CrossTenantAccessException(
                        $"Entity '{entry.Metadata.ClrType.Name}' belongs to tenant '{entry.Entity.TenantId}', " +
                        $"but the current tenant is '{currentTenant}'.");
            }
        }
    }
}
