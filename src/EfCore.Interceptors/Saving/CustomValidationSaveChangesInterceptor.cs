using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Runs all registered <see cref="IEntityValidator"/>s over every Added/Modified entity before
/// saving and aborts with an aggregated <see cref="EntityValidationException"/>.
/// </summary>
public class CustomValidationSaveChangesInterceptor(
    IReadOnlyList<IEntityValidator> validators) : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => -300;
    private readonly IReadOnlyList<IEntityValidator> _validators = validators;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Validate(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Validate(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    protected virtual void Validate(DbContext? context)
    {
        if (context is null || _validators.Count == 0)
        {
            return;
        }

        var failures = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var messages = _validators
                .SelectMany(v => v.Validate(entry.Entity))
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToArray();

            if (messages.Length > 0)
            {
                var key = $"{entry.Metadata.ClrType.Name}[{DescribeKey(entry)}]";
                if (failures.TryGetValue(key, out var existing))
                {
                    failures[key] = [.. existing, .. messages];
                }
                else
                {
                    failures[key] = messages;
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new EntityValidationException(failures);
        }
    }

    private static string DescribeKey(EntityEntry entry)
    {
        var pk = entry.Metadata.FindPrimaryKey();
        if (pk is null) return "no-key";
        return string.Join(",",
            pk.Properties
                .Select(p => entry.Property(p.Name).CurrentValue));
    }
}
