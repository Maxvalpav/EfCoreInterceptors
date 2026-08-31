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
    TimeProvider? clock = null) : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => -100;
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

            // Idempotency: already soft-deleted -> keep as unchanged
            if (entry.Entity.IsDeleted)
            {
                entry.State = EntityState.Unchanged;
                continue;
            }

            entry.State = EntityState.Modified;
            entry.Entity.IsDeleted = true;
            entry.Entity.DeletedAtUtc = now;
            entry.Entity.DeletedBy = user;

            // Do not touch Created* stamps if entity is also auditable
            if (entry.Entity is IAuditableEntity)
            {
                if (entry.Metadata.FindProperty(nameof(IAuditableEntity.CreatedAtUtc)) is not null)
                    entry.Property(nameof(IAuditableEntity.CreatedAtUtc)).IsModified = false;
                if (entry.Metadata.FindProperty(nameof(IAuditableEntity.CreatedBy)) is not null)
                    entry.Property(nameof(IAuditableEntity.CreatedBy)).IsModified = false;
            }
        }

        // Cascade safety: children without soft-delete that were cascade-deleted become orphans
        var orphans = context.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Deleted && e.Entity is not ISoftDeletableEntity)
            .ToList();
        if (orphans.Count > 0)
        {
            throw new SoftDeleteCascadeException(
                $"Soft-deleting parent would hard-delete {orphans.Count} child(ren) without ISoftDeletableEntity: {string.Join(", ", orphans.Select(o => o.Metadata.ClrType.Name))}");
        }
    }
}
