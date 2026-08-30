using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Aggregated DataAnnotations validation before the save: runs
/// Validator for every Added/Modified entity and aborts with a single
/// <see cref="EntityValidationException"/> listing ALL violations — instead of failing on the
/// first one at the database.
/// </summary>
public class ValidationSaveChangesInterceptor : SaveChangesInterceptor
{
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
        if (context is null)
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

            var validationContext = new ValidationContext(entry.Entity);
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(entry.Entity, validationContext, results, validateAllProperties: true))
            {
                var key = $"{entry.Metadata.ClrType.Name}[{DescribeKey(entry)}]";
                failures[key] = results
                    .Select(r => r.ErrorMessage ?? "Invalid.")
                    .ToArray();
            }
        }

        if (failures.Count > 0)
        {
            throw new EntityValidationException(failures);
        }
    }

    private static string DescribeKey(EntityEntry entry)
        => string.Join(",",
            entry.Metadata.FindPrimaryKey()!.Properties
                .Select(p => entry.Property(p.Name).CurrentValue));
}

/// <summary>All DataAnnotations violations found by ValidationSaveChangesInterceptor.</summary>
public sealed class EntityValidationException(IReadOnlyDictionary<string, string[]> failures)
    : Exception(BuildMessage(failures))
{
    /// <summary>Entity description -> list of violation messages.</summary>
    public IReadOnlyDictionary<string, string[]> Failures { get; } = failures;

    private static string BuildMessage(IReadOnlyDictionary<string, string[]> failures)
        => $"Validation failed for {failures.Count} entity instance(s). " +
           string.Join(" ", failures.Select(kv => $"{kv.Key}: {string.Join("; ", kv.Value)}"));
}
