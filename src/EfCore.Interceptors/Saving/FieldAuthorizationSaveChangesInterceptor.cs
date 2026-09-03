using System.Collections.Concurrent;
using System.Reflection;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Field-level authorization on write (03.3): changing a
/// <see cref="RequiresRoleAttribute"/> property without one of the roles throws
/// <see cref="FieldAuthorizationException"/> before anything reaches the database.
/// </summary>
public class FieldAuthorizationSaveChangesInterceptor(IRoleProvider roles) : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => -200;
    private readonly IRoleProvider _roles = roles;
    private static readonly ConcurrentDictionary<IReadOnlyEntityType, (IReadOnlyProperty Property, string[] Roles)[]> Cache = new();

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
        if (context is null) return;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            var guarded = Cache.GetOrAdd(entry.Metadata, static et =>
                et.GetProperties()
                    .Select(p => (Property: p, Attr: p.PropertyInfo?.GetCustomAttribute<RequiresRoleAttribute>()))
                    .Where(t => t.Attr is not null)
                    .Select(t => (t.Property, t.Attr!.Roles))
                    .ToArray());
            foreach (var (property, required) in guarded)
            {
                var prop = entry.Property(property.Name);
                var touched = entry.State == EntityState.Added
                    ? prop.CurrentValue is not null && !Equals(prop.CurrentValue, DefaultOf(prop.Metadata.ClrType))
                    : prop.IsModified;
                if (touched && !required.Any(_roles.IsInRole))
                    throw new FieldAuthorizationException(
                        $"Property '{entry.Metadata.ClrType.Name}.{property.Name}' requires one of roles [{string.Join(", ", required)}].");
            }
        }
    }

    private static object? DefaultOf(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;
}
