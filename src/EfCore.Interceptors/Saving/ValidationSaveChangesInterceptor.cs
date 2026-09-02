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

            ValidateEntry(entry, failures);
            // D-27: also validate owned/complex instances
            foreach (var complex in entry.ComplexProperties)
                ValidateComplex(complex, failures);
            foreach (var nav in entry.References.Where(r => r.TargetEntry?.Metadata.IsOwned() == true))
                if (nav.TargetEntry != null) ValidateEntry(nav.TargetEntry, failures);
        }

        void ValidateEntry(EntityEntry e, Dictionary<string,string[]> dict)
        {
            var validationContext = new ValidationContext(e.Entity);
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(e.Entity, validationContext, results, validateAllProperties: true))
            {
                var key = $"{e.Metadata.ClrType.Name}[{DescribeKey(e)}]";
                var messages = results.Select(r => r.ErrorMessage ?? "Invalid.").ToArray();
                if (dict.TryGetValue(key, out var existing))
                    dict[key] = [.. existing, .. messages];
                else
                    dict[key] = messages;
            }
        }

        void ValidateComplex(ComplexPropertyEntry c, Dictionary<string,string[]> dict)
        {
            if (c.ComplexProperties.Any())
                foreach (var n in c.ComplexProperties) ValidateComplex(n, dict);
            var pi = c.Metadata.PropertyInfo;
            if (pi?.GetValue(c.EntityEntry.Entity) is object complexObj)
            {
                var ctx = new ValidationContext(complexObj);
                var res = new List<ValidationResult>();
                if (!Validator.TryValidateObject(complexObj, ctx, res, true))
                {
                    var key = $"{c.Metadata.DeclaringType.ClrType.Name}.{c.Metadata.Name}";
                    var msgs = res.Select(r => r.ErrorMessage ?? "Invalid.").ToArray();
                    if (dict.TryGetValue(key, out var ex)) dict[key] = [.. ex, .. msgs];
                    else dict[key] = msgs;
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
