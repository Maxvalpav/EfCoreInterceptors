using System.Linq.Expressions;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Write-side row-level security guard (03.2): Added/Modified entities must satisfy the
/// predicate (compiled and evaluated client-side); violations throw
/// <see cref="RowLevelSecurityException"/>. Inside <see cref="ElevatedSession"/> the
/// guard is bypassed (writes are still stamped/audited by the other interceptors).
/// </summary>
public class RowLevelSecuritySaveChangesInterceptor<T>(
    Expression<Func<T, bool>> filter) : SaveChangesInterceptor, IOrderedInterceptor where T : class
{
    public int Order => -150;
    private readonly Func<T, bool> _compiled =
        (filter ?? throw new ArgumentNullException(nameof(filter))).Compile();

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

    private void Guard(DbContext? context)
    {
        if (context is null || ElevatedSession.IsElevated) return;
        foreach (var entry in context.ChangeTracker.Entries<T>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            bool allowed;
            try { allowed = _compiled(entry.Entity); }
            catch (Exception ex)
            {
                throw new RowLevelSecurityException(
                    $"Row-level predicate failed for '{typeof(T).Name}': {ex.Message}");
            }
            if (!allowed)
                throw new RowLevelSecurityException(
                    $"Write to '{typeof(T).Name}' violates the row-level security predicate. " +
                    "Use ElevatedSession.Elevate(reason) for system operations.");
        }
    }
}
