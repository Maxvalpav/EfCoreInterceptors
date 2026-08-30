using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Shadow-property audit: works without IAuditableEntity interface.
/// If the entity type has shadow columns named "CreatedAtUtc"/"CreatedBy"/"UpdatedAtUtc"/"UpdatedBy"
/// (configured via modelBuilder.Entity&lt;T&gt;().Property&lt;DateTimeOffset&gt;("CreatedAtUtc") etc.),
/// they are maintained automatically. Pairs with AuditSaveChangesInterceptor for interface-based entities.
/// </summary>
public class ShadowAuditSaveChangesInterceptor(
    Abstractions.ICurrentUserProvider? currentUserProvider = null,
    TimeProvider? clock = null) : SaveChangesInterceptor
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Abstractions.ICurrentUserProvider _users = currentUserProvider ?? Abstractions.StaticCurrentUserProvider.System;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    protected virtual void Apply(DbContext? context)
    {
        if (context is null) return;
        var now = _clock.GetUtcNow();
        var user = _users.UserName;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            var hasCreatedAt = entry.Metadata.FindProperty("CreatedAtUtc") is not null;
            var hasUpdatedAt = entry.Metadata.FindProperty("UpdatedAtUtc") is not null;
            if (!hasCreatedAt && !hasUpdatedAt) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    if (hasCreatedAt) entry.Property("CreatedAtUtc").CurrentValue = now;
                    if (entry.Metadata.FindProperty("CreatedBy") is not null) entry.Property("CreatedBy").CurrentValue = user;
                    if (hasUpdatedAt) entry.Property("UpdatedAtUtc").CurrentValue = now;
                    if (entry.Metadata.FindProperty("UpdatedBy") is not null) entry.Property("UpdatedBy").CurrentValue = user;
                    break;
                case EntityState.Modified:
                    if (hasUpdatedAt) entry.Property("UpdatedAtUtc").CurrentValue = now;
                    if (entry.Metadata.FindProperty("UpdatedBy") is not null) entry.Property("UpdatedBy").CurrentValue = user;
                    if (hasCreatedAt) entry.Property("CreatedAtUtc").IsModified = false;
                    if (entry.Metadata.FindProperty("CreatedBy") is not null) entry.Property("CreatedBy").IsModified = false;
                    break;
            }
        }
    }
}
