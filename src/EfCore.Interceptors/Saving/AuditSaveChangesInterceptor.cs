using System.Runtime.CompilerServices;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Automatically maintains audit columns on <see cref="IAuditableEntity"/> entities:
/// on insert fills CreatedAtUtc/CreatedBy (and Updated* with the same values);
/// on update refreshes UpdatedAtUtc/UpdatedBy and protects Created* from being overwritten.
/// A change limited to an owned or complex property also refreshes Updated* on the owner root.
/// </summary>
public class AuditSaveChangesInterceptor(
    ICurrentUserProvider? currentUserProvider = null,
    TimeProvider? clock = null) : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => 0;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ICurrentUserProvider _users = currentUserProvider ?? StaticCurrentUserProvider.System;
    private static readonly ConditionalWeakTable<IEntityType, PropNames> _propCache = new();
    private sealed class PropNames(string? createdAt, string? createdBy)
    {
        public string? CreatedAt { get; } = createdAt;
        public string? CreatedBy { get; } = createdBy;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        try { ApplyAuditStamps(eventData.Context); }
        catch { if (eventData.Context != null) ChangeTrackerSnapshot.End(eventData.Context); throw; }
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        try { ApplyAuditStamps(eventData.Context); }
        catch { if (eventData.Context != null) ChangeTrackerSnapshot.End(eventData.Context); throw; }
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

    protected virtual void ApplyAuditStamps(DbContext? context)
    {
        if (context is null) return;
        var now = _clock.GetUtcNow();
        var user = _users.UserName;
        foreach (var entry in ChangeTrackerSnapshot.Get<IAuditableEntity>(context))
        {
            var entity = (IAuditableEntity)entry.Entity;
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entity.CreatedAtUtc == default) entity.CreatedAtUtc = now;
                    if (entity.CreatedBy is null) entity.CreatedBy = user;
                    if (entity.UpdatedAtUtc is null) entity.UpdatedAtUtc = now;
                    if (entity.UpdatedBy is null) entity.UpdatedBy = user;
                    break;
                case EntityState.Modified:
                    entity.UpdatedAtUtc = now;
                    entity.UpdatedBy = user;
                    ProtectCreated(entry);
                    break;
                case EntityState.Unchanged when HasOwnedOrComplexChanges(entry):
                    // Owned entries are separate EntityEntries: the root stays Unchanged while
                    // only Address.City changed — the audit stamp would be skipped. Touch only
                    // the Updated* columns so the UPDATE stays narrow.
                    entity.UpdatedAtUtc = now;
                    entity.UpdatedBy = user;
                    entry.Property(nameof(IAuditableEntity.UpdatedAtUtc)).IsModified = true;
                    entry.Property(nameof(IAuditableEntity.UpdatedBy)).IsModified = true;
                    ProtectCreated(entry);
                    break;
            }
        }
    }

    private static bool HasOwnedOrComplexChanges(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        foreach (var reference in entry.References)
        {
            Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry? target;
            try { target = reference.TargetEntry; }
            catch (InvalidOperationException) { continue; }
            if (target is not null && target.State != EntityState.Unchanged && target.Metadata.IsOwned())
                return true;
        }
        try
        {
            if (entry.ComplexProperties.Any(cp => cp.IsModified))
                return true;
        }
        catch (InvalidOperationException) { /* provider without complex-type support */ }
        return false;
    }

    private void ProtectCreated(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        var cached = _propCache.GetValue(entry.Metadata, t => new PropNames(
            t.FindProperty(nameof(IAuditableEntity.CreatedAtUtc))?.Name,
            t.FindProperty(nameof(IAuditableEntity.CreatedBy))?.Name));
        if (cached.CreatedAt != null) entry.Property(cached.CreatedAt).IsModified = false;
        if (cached.CreatedBy != null) entry.Property(cached.CreatedBy).IsModified = false;
    }
}
