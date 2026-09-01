using System.ComponentModel.DataAnnotations;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Aggregated DataAnnotations validation before the save: runs
/// Validator for every Added/Modified entity and aborts with a single
/// <see cref="EntityValidationException"/> listing ALL violations — instead of failing on the
/// first one at the database.
/// </summary>
public class ValidationSaveChangesInterceptor : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => -300;
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

    public override int SavedChanges(SaveChangesCompletedEventData e, int result){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); return base.SavedChanges(e,result); }
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData e, int result, CancellationToken ct=default){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); return base.SavedChangesAsync(e,result,ct); }
    public override void SaveChangesFailed(DbContextErrorEventData e){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); base.SaveChangesFailed(e); }
    public override Task SaveChangesFailedAsync(DbContextErrorEventData e, CancellationToken ct=default){ if(e.Context!=null) ChangeTrackerSnapshot.End(e.Context); return base.SaveChangesFailedAsync(e,ct); }

    protected virtual void Validate(DbContext? context)
    {
        if (context is null) return;
        var failures = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var entry in ChangeTrackerSnapshot.GetAll(context))
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            var validationContext = new ValidationContext(entry.Entity);
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(entry.Entity, validationContext, results, validateAllProperties: true))
            {
                var key = $"{entry.Metadata.ClrType.Name}[{DescribeKey(entry)}]";
                var messages = results.Select(r => r.ErrorMessage ?? "Invalid.").ToArray();
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
