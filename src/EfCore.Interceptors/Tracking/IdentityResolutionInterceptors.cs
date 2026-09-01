using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Tracking;

/// <summary>How identity conflicts are resolved.</summary>
public enum IdentityResolutionMode
{
    /// <summary>Incoming values overwrite tracked state (last-write-wins).</summary>
    Overwrite,
    /// <summary>Tracked state wins; incoming values are ignored.</summary>
    IgnoreIncoming
}

/// <summary>
/// Resolves identity conflicts by copying incoming values over the already-tracked instance
/// (last-write-wins). Requires enabling resolution on the change tracker:
/// <c>context.ChangeTracker.IdentityResolutionBehavior = IdentityResolutionBehavior.UpdateTracked;</c>
/// </summary>
public sealed class OverwriteIdentityResolutionInterceptor : IIdentityResolutionInterceptor
{
    public void UpdateTrackedInstance(
        IdentityResolutionInterceptionData interceptionData,
        EntityEntry existingEntry,
        object newEntity)
        => existingEntry.CurrentValues.SetValues(newEntity);
}

/// <summary>
/// Resolves identity conflicts by keeping the already-tracked instance untouched
/// (first-write-wins / cache semantics). Requires:
/// <c>context.ChangeTracker.IdentityResolutionBehavior = IdentityResolutionBehavior.UpdateTracked;</c>
/// </summary>
public sealed class IgnoreIncomingIdentityResolutionInterceptor : IIdentityResolutionInterceptor
{
    public void UpdateTrackedInstance(
        IdentityResolutionInterceptionData interceptionData,
        EntityEntry existingEntry,
        object newEntity)
    {
        // Keep the existing tracked state; the incoming instance is discarded.
    }
}
