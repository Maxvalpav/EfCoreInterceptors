using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ApplyAuditStamps(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyAuditStamps(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    protected virtual void ApplyAuditStamps(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var user = _users.UserName;

        foreach (var entry in context.ChangeTracker.Entries<IAuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    // Respect pre-filled import/migration values (logic-audit #2)
                    if (entry.Entity.CreatedAtUtc == default) entry.Entity.CreatedAtUtc = now;
                    if (entry.Entity.CreatedBy is null) entry.Entity.CreatedBy = user;
                    // Updated* for new entities mirrors Created* if not already set
                    if (entry.Entity.UpdatedAtUtc is null) entry.Entity.UpdatedAtUtc = now;
                    if (entry.Entity.UpdatedBy is null) entry.Entity.UpdatedBy = user;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAtUtc = now;
                    entry.Entity.UpdatedBy = user;
                    // Creation stamps are immutable once written.
                    if (entry.Metadata.FindProperty(nameof(IAuditableEntity.CreatedAtUtc)) is not null)
                        entry.Property(nameof(IAuditableEntity.CreatedAtUtc)).IsModified = false;
                    if (entry.Metadata.FindProperty(nameof(IAuditableEntity.CreatedBy)) is not null)
                        entry.Property(nameof(IAuditableEntity.CreatedBy)).IsModified = false;
                    break;
            }
        }
    }
}
