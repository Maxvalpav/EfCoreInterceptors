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
public class MultiTenancySaveChangesInterceptor(ITenantProvider tenantProvider) : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => -150;
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

    public override int SavedChanges(SaveChangesCompletedEventData e, int r){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); return base.SavedChanges(e,r); }
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData e, int r, CancellationToken ct=default){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); return base.SavedChangesAsync(e,r,ct); }
    public override void SaveChangesFailed(DbContextErrorEventData e){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); base.SaveChangesFailed(e); }
    public override Task SaveChangesFailedAsync(DbContextErrorEventData e, CancellationToken ct=default){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); return base.SaveChangesFailedAsync(e,ct); }

    protected virtual void ApplyTenantRules(DbContext? context)
    {
        if (context is null) return;
        var currentTenant = _tenants.CurrentTenantId;
        foreach (var entry in ChangeTrackerSnapshot.Get<ITenantEntity>(context))
        {
            var entity = (ITenantEntity)entry.Entity;
            if (entry.State == EntityState.Modified)
            {
                var prop = entry.Property(nameof(ITenantEntity.TenantId));
                if (!Equals(prop.OriginalValue, prop.CurrentValue))
                    throw new CrossTenantAccessException($"TenantId is immutable for '{entry.Metadata.ClrType.Name}'. Original='{prop.OriginalValue}', Current='{prop.CurrentValue}'.");
            }
            switch (entry.State)
            {
                case EntityState.Added:
                    if (currentTenant is null) throw new CrossTenantAccessException($"Cannot insert '{entry.Metadata.ClrType.Name}' without current tenant. CurrentTenantId is null.");
                    if (entity.TenantId is not null && !string.Equals(entity.TenantId, currentTenant, StringComparison.Ordinal))
                        throw new CrossTenantAccessException($"Entity '{entry.Metadata.ClrType.Name}' pre-set to tenant '{entity.TenantId}', but current tenant is '{currentTenant}'.");
                    entity.TenantId = currentTenant;
                    break;
                case EntityState.Modified when !string.Equals(entity.TenantId, currentTenant, StringComparison.Ordinal):
                    throw new CrossTenantAccessException($"Entity '{entry.Metadata.ClrType.Name}' belongs to tenant '{entity.TenantId}', but the current tenant is '{currentTenant}'.");
                case EntityState.Deleted when !string.Equals(entity.TenantId, currentTenant, StringComparison.Ordinal):
                    throw new CrossTenantAccessException($"Cannot delete '{entry.Metadata.ClrType.Name}' of tenant '{entity.TenantId}' from current tenant '{currentTenant}'.");
            }
        }
    }
}
