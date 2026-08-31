using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Safety net against runaway bulk operations: throws <see cref="MassOperationException"/> when a
/// single SaveChanges would affect more rows than allowed per state (e.g. a missing WHERE clause
/// wiping out a whole table through loaded entities).
/// </summary>
public class MassOperationGuardSaveChangesInterceptor(
    int maxAdded = 100,
    int maxModified = 100,
    int maxDeleted = 100) : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => -200;
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    protected virtual void Guard(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var counts = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .GroupBy(e => e.State)
            .ToDictionary(g => g.Key, g => g.Count());

        var problems = new List<string>();
        Check(counts, EntityState.Added, maxAdded, problems);
        Check(counts, EntityState.Modified, maxModified, problems);
        Check(counts, EntityState.Deleted, maxDeleted, problems);

        if (problems.Count > 0)
        {
            throw new MassOperationException(
                "SaveChanges aborted: " + string.Join(" ", problems));
        }
    }

    private static void Check(Dictionary<EntityState, int> counts, EntityState state, int limit, List<string> problems)
    {
        if (counts.TryGetValue(state, out var count) && count > limit)
        {
            problems.Add($"{state}: {count} entries exceed the limit of {limit}.");
        }
    }
}
