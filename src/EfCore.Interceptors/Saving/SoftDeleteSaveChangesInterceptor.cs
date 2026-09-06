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
    TimeProvider? clock = null,
    SoftDeleteOrphanPolicy orphanPolicy = SoftDeleteOrphanPolicy.Throw) : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => -100;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ICurrentUserProvider _users = currentUserProvider ?? StaticCurrentUserProvider.System;
    private readonly SoftDeleteOrphanPolicy _orphanPolicy = orphanPolicy;

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

    protected virtual void ConvertDeletes(DbContext? context)
    {
        if (context is null) return;
        var now = _clock.GetUtcNow();
        var user = _users.UserName;
        // Validate BEFORE mutating the tracker: throwing after IsDeleted=true leaves
        // ChangeTracker in a half-mutated state and the next SaveChanges would persist garbage.
        // Note: the check is intentionally coarse (any hard-deletable entity in the same unit of
        // work trips it) — narrowing it to true cascade orphans would require loading navigation
        // graphs; use AllowHardDelete for mixed units of work instead.
        var orphans = ChangeTrackerSnapshot.GetAll(context)
            .Where(e => e.State == EntityState.Deleted && e.Entity is not ISoftDeletableEntity)
            .ToList();
        if (orphans.Count > 0 && _orphanPolicy == SoftDeleteOrphanPolicy.Throw)
        {
            throw new SoftDeleteCascadeException(
                $"Soft-deleting parent would hard-delete {orphans.Count} child(ren) without ISoftDeletableEntity: {string.Join(", ", orphans.Select(o => o.Metadata.ClrType.Name))}");
        }
        foreach (var entry in ChangeTrackerSnapshot.Get<ISoftDeletableEntity>(context))
        {
            if (entry.State != EntityState.Deleted) continue;
            var entity = (ISoftDeletableEntity)entry.Entity;
            if (entity.IsDeleted)
            {
                // Already soft-deleted: keep other pending edits instead of dropping them via Unchanged.
                entry.State = EntityState.Modified;
                entry.Property(nameof(ISoftDeletableEntity.IsDeleted)).IsModified = false;
                continue;
            }
            entry.State = EntityState.Modified;
            entity.IsDeleted = true;
            entity.DeletedAtUtc = now;
            entity.DeletedBy = user;
            if (entity is IAuditableEntity)
            {
                if (entry.Metadata.FindProperty(nameof(IAuditableEntity.CreatedAtUtc)) is not null)
                    entry.Property(nameof(IAuditableEntity.CreatedAtUtc)).IsModified = false;
                if (entry.Metadata.FindProperty(nameof(IAuditableEntity.CreatedBy)) is not null)
                    entry.Property(nameof(IAuditableEntity.CreatedBy)).IsModified = false;
            }
        }
    }
}
