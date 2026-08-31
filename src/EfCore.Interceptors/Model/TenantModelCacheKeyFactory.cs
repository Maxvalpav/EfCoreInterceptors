using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EfCore.Interceptors.Model;

/// <summary>
/// Fixes tenant filter "sticking" in EF model cache:
/// by default EF caches model once per DbContext type, so capturing ITenantProvider.CurrentTenantId
/// in ApplyTenantFilters would freeze first tenant's id.
/// This factory makes model cache key include CurrentTenantId, so each tenant gets its own model variant.
/// Register via options.ReplaceService&lt;IModelCacheKeyFactory, TenantModelCacheKeyFactory&gt;().
/// Preferred alternative: use DbContext.CurrentTenantId property in HasQueryFilter directly (no external capture).
/// </summary>
public class TenantModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        // Preserve default behavior for designTime, otherwise include tenant.
        var tenantId = (context as ITenantProviderAccessor)?.CurrentTenantId
                       ?? context.GetService<ITenantProvider>()?.CurrentTenantId;

        // Use record for proper equality.
        return new TenantCacheKey(context.GetType(), tenantId, designTime);
    }

    private sealed record TenantCacheKey(Type ContextType, string? TenantId, bool DesignTime);
}

/// <summary>
/// Optional: implement this on your DbContext to expose current tenant without service locator
/// (avoids GetService). Example: public class AppDbContext : DbContext, ITenantProviderAccessor { public string? CurrentTenantId { get; set; } }
/// </summary>
public interface ITenantProviderAccessor
{
    string? CurrentTenantId { get; }
}
