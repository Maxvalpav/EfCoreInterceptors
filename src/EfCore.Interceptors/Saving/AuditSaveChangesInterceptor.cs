using System.Collections.Concurrent;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Automatically maintains audit columns on <see cref="IAuditableEntity"/> entities:
/// on insert fills CreatedAtUtc/CreatedBy (and Updated* with the same values);
/// on update refreshes UpdatedAtUtc/UpdatedBy and protects Created* from being overwritten.
/// </summary>
public class AuditSaveChangesInterceptor(
    ICurrentUserProvider? currentUserProvider = null,
    TimeProvider? clock = null) : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => 0;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ICurrentUserProvider _users = currentUserProvider ?? StaticCurrentUserProvider.System;
    private static readonly ConcurrentDictionary<IEntityType, (IProperty? CreatedAt, IProperty? CreatedBy)> _propCache = new();

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

    public override int SavedChanges(SaveChangesCompletedEventData e, int result){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); return base.SavedChanges(e,result); }
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData e, int result, CancellationToken ct=default){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); return base.SavedChangesAsync(e,result,ct); }
    public override void SaveChangesFailed(DbContextErrorEventData e){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); base.SaveChangesFailed(e); }
    public override Task SaveChangesFailedAsync(DbContextErrorEventData e, CancellationToken ct=default){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); return base.SaveChangesFailedAsync(e,ct); }

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
                    var cached = _propCache.GetOrAdd(entry.Metadata, t => (t.FindProperty(nameof(IAuditableEntity.CreatedAtUtc)), t.FindProperty(nameof(IAuditableEntity.CreatedBy))));
                    if (cached.CreatedAt != null) entry.Property(cached.CreatedAt.Name).IsModified = false;
                    if (cached.CreatedBy != null) entry.Property(cached.CreatedBy.Name).IsModified = false;
                    break;
            }
        }
    }
}
