using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Absolute delete protection: any Delete of an <see cref="IProtectedEntity"/> is rejected with
/// <see cref="ProtectedEntityException"/> before reaching the database — even when soft delete
/// would later convert it. Use for records that must never disappear (financial, legal).
/// </summary>
public class DeleteGuardSaveChangesInterceptor : SaveChangesInterceptor
{
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

        foreach (var entry in context.ChangeTracker.Entries<IProtectedEntity>())
        {
            if (entry.State == EntityState.Deleted)
            {
                throw new ProtectedEntityException(
                    $"Entity '{entry.Metadata.ClrType.Name}' is protected and cannot be deleted.");
            }
        }
    }
}

/// <summary>
/// Append-only enforcement: modifications and deletes of <see cref="IImmutableEntity"/>
/// (audit history, posted ledger rows) are rejected with <see cref="ImmutableEntityException"/>.
/// Inserts remain allowed.
/// </summary>
public class ImmutableEntityGuardSaveChangesInterceptor : SaveChangesInterceptor
{
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

        foreach (var entry in context.ChangeTracker.Entries<IImmutableEntity>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new ImmutableEntityException(
                    $"Entity '{entry.Metadata.ClrType.Name}' is immutable; " +
                    $"{entry.State} operations are not allowed.");
            }
        }
    }
}
