using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Maintains the application-managed optimistic-concurrency counter:
/// every Modified <see cref="IVersionedEntity"/> gets Version + 1 before the save.
/// Pair with a mapped concurrency token to actually reject stale writes.
/// </summary>
public class VersionIncrementSaveChangesInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Increment(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Increment(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    protected virtual void Increment(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<IVersionedEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.Version++;
            }
        }
    }
}
