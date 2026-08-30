using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Trims leading/trailing whitespace from all string properties on Added/Modified entities.
/// Prevents "  alice@example.com " from becoming a distinct value vs "alice@example.com".
/// Runs before validation/encryption, so normalized values are what gets validated.
/// </summary>
public class StringTrimmingSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly Func<string, string?>? _normalize;

    /// <param name="normalize">Optional extra normalization (e.g. ToLowerInvariant) applied after Trim. Null = trim only.</param>
    public StringTrimmingSaveChangesInterceptor(Func<string, string?>? normalize = null) => _normalize = normalize;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Trim(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Trim(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    protected virtual void Trim(DbContext? context)
    {
        if (context is null) return;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            foreach (var prop in entry.Properties)
            {
                if (prop.Metadata.ClrType != typeof(string)) continue;
                if (entry.State == EntityState.Modified && !prop.IsModified) continue;
                if (prop.CurrentValue is not string s) continue;
                var trimmed = s.Trim();
                if (_normalize is not null) trimmed = _normalize(trimmed) ?? trimmed;
                if (!ReferenceEquals(trimmed, s)) prop.CurrentValue = trimmed;
            }
        }
    }
}
