using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Maintains the application-managed optimistic-concurrency counter:
/// every Modified <see cref="IVersionedEntity"/> gets Version + 1 before the save.
/// Pair with a mapped concurrency token to actually reject stale writes.
/// </summary>
public class VersionIncrementSaveChangesInterceptor : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => 50;
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

    public override int SavedChanges(SaveChangesCompletedEventData e, int result)
    {
        if (e.Context != null) ChangeTrackerSnapshot.End(e.Context);
        return base.SavedChanges(e, result);
    }
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData e, int result, CancellationToken ct = default)
    {
        if (e.Context != null) ChangeTrackerSnapshot.End(e.Context);
        return base.SavedChangesAsync(e, result, ct);
    }
    public override void SaveChangesFailed(DbContextErrorEventData e)
    {
        if (e.Context != null) ChangeTrackerSnapshot.End(e.Context);
        base.SaveChangesFailed(e);
    }
    public override Task SaveChangesFailedAsync(DbContextErrorEventData e, CancellationToken ct = default)
    {
        if (e.Context != null) ChangeTrackerSnapshot.End(e.Context);
        return base.SaveChangesFailedAsync(e, ct);
    }

    protected virtual void Increment(DbContext? context)
    {
        if (context is null) return;
        if (Saving.ConcurrencyRetrySaveChangesInterceptor.IsRetrying(context) || Saving.ChangeLogSaveChangesInterceptor.IsPatching(context)) return;
        foreach (var entry in ChangeTrackerSnapshot.Get<IVersionedEntity>(context))
        {
            if (entry.State == EntityState.Modified)
            {
                var prop = entry.Property(nameof(IVersionedEntity.Version));
                if (prop.IsModified) continue;
                ((IVersionedEntity)entry.Entity).Version++;
                prop.IsModified = true;
            }
        }
    }
}
