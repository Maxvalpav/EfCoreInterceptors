using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Expand-contract dual write (03.16): on Added/Modified, every
/// <see cref="MigratedFromAttribute"/> NEW property mirrors its value into the OLD
/// column, so old and new code versions read consistent data during the migration
/// window. Runs at audit stage (Order 0); retired together with the old column.
/// </summary>
public class ExpandContractSaveChangesInterceptor : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => 0;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        DualWrite(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        DualWrite(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void DualWrite(DbContext? context)
    {
        if (context is null) return;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            foreach (var (neu, old) in ExpandContractCache.For(entry.Entity.GetType()))
            {
                var newValue = neu.GetValue(entry.Entity);
                if (IsDefault(newValue, neu.PropertyType)) continue;
                if (Equals(old.GetValue(entry.Entity), newValue)) continue;
                old.SetValue(entry.Entity, newValue);
                try { entry.Property(old.Name).IsModified = true; }
                catch (InvalidOperationException) { /* unmapped/shadow — value still set on the CLR object */ }
            }
        }
    }

    private static bool IsDefault(object? value, Type type)
        => value is null || (type.IsValueType && Equals(value, Activator.CreateInstance(type)));
}
