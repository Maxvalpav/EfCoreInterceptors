using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Abstractions;

/// <summary>Data sensitivity tier for a property (03.4, GDPR/HIPAA evidence).</summary>
public enum Sensitivity
{
    Unclassified,
    Internal,
    Confidential,
    Pii,
    Phi,
    Secret
}

/// <summary>
/// Declares the sensitivity of a property: feeds the GDPR/retention engine,
/// the data-catalog report and log redaction. Example:
/// <c>[DataClassification(Sensitivity.Pii, Retention = "365d")]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DataClassificationAttribute(Sensitivity sensitivity) : Attribute
{
    public Sensitivity Sensitivity { get; } = sensitivity;

    /// <summary>Retention hint, e.g. "30d", "365d", "forever". Enforced by maintenance jobs.</summary>
    public string? Retention { get; set; }
}

/// <summary>Marks the property holding the data-subject id for GDPR erasure (03.5).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SubjectIdentifierAttribute : Attribute;

/// <summary>One classified column for catalog export.</summary>
public sealed record DataClassificationEntry(
    string EntityName,
    string PropertyName,
    string Table,
    string Column,
    Sensitivity Sensitivity,
    string? Retention);

/// <summary>Export target for classification reports (Microsoft Purview / DataHub / OpenMetadata).</summary>
public interface IDataCatalogSink
{
    Task ExportAsync(IReadOnlyList<DataClassificationEntry> entries, CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the "where do PII/PHI live" report (03.4) from the EF model — input for
/// GDPR/HIPAA audits and catalog sinks. Unattributed properties are skipped.
/// </summary>
public static class DataClassificationReport
{
    public static IReadOnlyList<DataClassificationEntry> Generate(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var result = new List<DataClassificationEntry>();
        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.IsOwned()) continue;
            var table = entityType.GetTableName() ?? entityType.ClrType.Name;
            var modelProps = entityType.GetProperties().ToDictionary(p => p.Name, StringComparer.Ordinal);
            // Scan CLR properties directly (robust when PropertyInfo mapping is unavailable);
            // fall back to model metadata for column names.
            foreach (var pi in entityType.ClrType.GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = pi.GetCustomAttribute<DataClassificationAttribute>();
                if (attr is null) continue;
                modelProps.TryGetValue(pi.Name, out var mapped);
                result.Add(new DataClassificationEntry(
                    entityType.ClrType.Name,
                    pi.Name,
                    table,
                    mapped?.GetColumnName() ?? pi.Name,
                    attr.Sensitivity,
                    attr.Retention));
            }
        }
        return result;
    }
}
