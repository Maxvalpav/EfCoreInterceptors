using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Tracking;

/// <summary>
/// Identity resolution with "last write wins" semantics based on audit stamps:
/// the instance whose UpdatedAtUtc is newer survives. Requires both instances to implement
/// <see cref="IAuditableEntity"/>; for non-auditable types the tracked instance is kept.
/// </summary>
public sealed class NewestWinsIdentityResolutionInterceptor : IIdentityResolutionInterceptor
{
    public void UpdateTrackedInstance(
        IdentityResolutionInterceptionData interceptionData,
        EntityEntry existingEntry,
        object newEntity)
    {
        if (existingEntry.Entity is not IAuditableEntity existing ||
            newEntity is not IAuditableEntity incoming)
        {
            return; // no timestamps to compare — keep tracked state
        }

        // Nullable UpdatedAtUtc: null = never updated, treat as oldest
        var incomingTime = incoming.UpdatedAtUtc ?? incoming.CreatedAtUtc;
        var existingTime = existing.UpdatedAtUtc ?? existing.CreatedAtUtc;
        if (incomingTime > existingTime)
        {
            existingEntry.CurrentValues.SetValues(newEntity);
        }
    }
}
