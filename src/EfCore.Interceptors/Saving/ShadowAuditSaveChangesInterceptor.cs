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
    TimeProvider? clock = null) : SaveChangesInterceptor, Abstractions.IOrderedInterceptor
{
    public int Order => 0;
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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Microsoft.EntityFrameworkCore.Metadata.IEntityType, (bool HasCreatedAt, bool HasCreatedBy, bool HasUpdatedAt, bool HasUpdatedBy)> _cache = new();

    protected virtual void Apply(DbContext? context)
    {
        if (context is null) return;
        var now = _clock.GetUtcNow();
        var user = _users.UserName;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            var flags = _cache.GetOrAdd(entry.Metadata, static et => (
                HasCreatedAt: et.FindProperty("CreatedAtUtc") is not null,
                HasCreatedBy: et.FindProperty("CreatedBy") is not null,
                HasUpdatedAt: et.FindProperty("UpdatedAtUtc") is not null,
                HasUpdatedBy: et.FindProperty("UpdatedBy") is not null));

            if (!flags.HasCreatedAt && !flags.HasCreatedBy && !flags.HasUpdatedAt && !flags.HasUpdatedBy) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    if (flags.HasCreatedAt) SetShadowValue(entry, "CreatedAtUtc", now);
                    if (flags.HasCreatedBy) entry.Property("CreatedBy").CurrentValue = user;
                    if (flags.HasUpdatedAt) SetShadowValue(entry, "UpdatedAtUtc", now);
                    if (flags.HasUpdatedBy) entry.Property("UpdatedBy").CurrentValue = user;
                    break;
                case EntityState.Modified:
                    if (flags.HasUpdatedAt) SetShadowValue(entry, "UpdatedAtUtc", now);
                    if (flags.HasUpdatedBy) entry.Property("UpdatedBy").CurrentValue = user;
                    if (flags.HasCreatedAt) entry.Property("CreatedAtUtc").IsModified = false;
                    if (flags.HasCreatedBy) entry.Property("CreatedBy").IsModified = false;
                    break;
            }
        }
    }

    private static void SetShadowValue(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string propertyName, DateTimeOffset value)
    {
        var prop = entry.Metadata.FindProperty(propertyName);
        if (prop is null) return;
        var clrType = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
        object? converted = clrType == typeof(DateTimeOffset) ? value
            : clrType == typeof(DateTime) ? value.UtcDateTime
            : clrType == typeof(string) ? value.ToString("O")
            : Convert.ChangeType(value, clrType, System.Globalization.CultureInfo.InvariantCulture);
        entry.Property(propertyName).CurrentValue = converted;
    }
}
