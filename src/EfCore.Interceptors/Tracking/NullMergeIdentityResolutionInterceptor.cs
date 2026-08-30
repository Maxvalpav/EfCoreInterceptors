using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Tracking;

/// <summary>
/// Null-preserving identity merge: incoming values overwrite only properties that are currently
/// null/default on the tracked instance — tracked non-empty data always wins.
/// </summary>
public sealed class NullMergeIdentityResolutionInterceptor : IIdentityResolutionInterceptor
{
    public void UpdateTrackedInstance(
        IdentityResolutionInterceptionData interceptionData,
        EntityEntry existingEntry,
        object newEntity)
    {
        foreach (var property in existingEntry.Properties)
        {
            var incoming = ReadIncoming(property.Metadata, newEntity);

            if (incoming is null || incoming is DBNull ||
                (incoming is string s && s.Length == 0))
            {
                continue;
            }

            if (existingEntry.CurrentValues[property.Metadata] is null or DBNull ||
                existingEntry.CurrentValues[property.Metadata] is string current && current.Length == 0)
            {
                existingEntry.CurrentValues[property.Metadata] = incoming;
            }
        }
    }

    private static object? ReadIncoming(IProperty property, object entity)
        => property.PropertyInfo?.GetValue(entity)
           ?? property.FieldInfo?.GetValue(entity);
}
