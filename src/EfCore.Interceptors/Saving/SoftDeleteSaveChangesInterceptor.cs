using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Converts hard deletes of <see cref="ISoftDeletableEntity"/> entities into updates that
/// set IsDeleted/DeletedAtUtc/DeletedBy, so history is never lost.
/// Add a global query filter (<c>HasQueryFilter(e =&gt; !e.IsDeleted)</c>) so soft-deleted
/// rows disappear from reads; this interceptor only handles the write side.
/// </summary>
public class SoftDeleteSaveChangesInterceptor(
    ICurrentUserProvider? currentUserProvider = null,
    TimeProvider? clock = null) : SaveChangesInterceptor
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ICurrentUserProvider _users = currentUserProvider ?? StaticCurrentUserProvider.System;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ConvertDeletes(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ConvertDeletes(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    protected virtual void ConvertDeletes(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var user = _users.UserName;

        foreach (var entry in context.ChangeTracker.Entries<ISoftDeletableEntity>())
        {
            if (entry.State != EntityState.Deleted)
            {
                continue;
            }

            entry.State = EntityState.Modified;
            entry.Entity.IsDeleted = true;
            entry.Entity.DeletedAtUtc = now;
            entry.Entity.DeletedBy = user;
        }
    }
}
