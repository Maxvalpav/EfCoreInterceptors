using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors;

/// <summary>GDPR "right to be forgotten" strategy (03.5).</summary>
public enum ForgetStrategy
{
    /// <summary>Replace PII with keyed SHA-256 (referential shape preserved, value unrecoverable).</summary>
    Pseudonymize,
    /// <summary>Set PII to null (nullable) or empty (non-nullable strings).</summary>
    Erase
}

/// <summary>
/// GDPR erasure across all entities carrying a <see cref="SubjectIdentifierAttribute"/> (03.5):
/// finds every row whose subject-id property equals <paramref name="subjectId"/> and
/// rewrites sensitive string properties (<c>[DataClassification(Pii/Phi/Secret)]</c>,
/// <c>[Encrypted]</c>, <c>[Masked]</c>) plus the identifier itself, then saves once.
/// Enable <c>WithChangeLog</c> to keep an audit trail of the erasure diffs.
/// </summary>
public static class GdprExtensions
{
    private static readonly MethodInfo SetMethod = typeof(DbContext)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .First(m => m.Name == nameof(DbContext.Set) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);

    /// <returns>Number of rows rewritten.</returns>
    public static async Task<int> ForgetSubjectAsync(
        this DbContext db,
        string subjectId,
        ForgetStrategy strategy = ForgetStrategy.Pseudonymize,
        string? salt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrEmpty(subjectId)) throw new ArgumentException("Subject id is required.", nameof(subjectId));
        if (strategy == ForgetStrategy.Pseudonymize && string.IsNullOrEmpty(salt))
            throw new ArgumentException("Pseudonymize requires a salt (use a per-deployment secret).", nameof(salt));

        var affected = 0;
        foreach (var entityType in db.Model.GetEntityTypes())
        {
            if (entityType.IsOwned()) continue;
            var clrType = entityType.ClrType;
            if (!clrType.IsClass) continue;
            var idProp = clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.PropertyType == typeof(string)
                    && p.GetCustomAttribute<SubjectIdentifierAttribute>() is not null);
            if (idProp is null) continue;

            var sensitive = clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string) && p.SetMethod is not null && IsSensitive(p))
                .ToArray();
            var targets = sensitive.Append(idProp).Distinct().ToArray();
            if (targets.Length == 0) continue;

            var query = (IQueryable<object>)SetMethod.MakeGenericMethod(clrType).Invoke(db, null)!;
            var rows = await query
                .Where(e => EF.Property<string>(e, idProp.Name) == subjectId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                foreach (var prop in targets)
                {
                    var current = (string?)prop.GetValue(row);
                    if (current is null) continue;
                    // Erase writes empty (never null) so non-null columns stay valid.
                    prop.SetValue(row, strategy == ForgetStrategy.Pseudonymize
                        ? Pseudonymize(current, salt!)
                        : string.Empty);
                }
                affected++;
            }
        }

        if (affected > 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    private static bool IsSensitive(PropertyInfo prop)
    {
        var dc = prop.GetCustomAttribute<DataClassificationAttribute>();
        if (dc is not null && dc.Sensitivity is Sensitivity.Pii or Sensitivity.Phi or Sensitivity.Secret)
            return true;
        if (prop.GetCustomAttribute<EncryptedAttribute>() is not null) return true;
        if (prop.GetCustomAttribute<MaskedAttribute>() is not null) return true;
        return false;
    }

    internal static string Pseudonymize(string value, string salt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(salt + "|" + value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
