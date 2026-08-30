using System.Reflection;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Materialization;

/// <summary>
/// DLP masking on materialization: [Masked] string properties are masked via IMaskingPolicy.
/// Ideal for non-production or API DTOs where raw PII must not leak.
/// </summary>
public class MaskingMaterializationInterceptor(IMaskingPolicy? policy = null) : IMaterializationInterceptor
{
    private readonly IMaskingPolicy _policy = policy ?? new DefaultMaskingPolicy();

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        foreach (var prop in MaskedProps(materializationData.EntityType))
        {
            if (prop.GetValue(entity) is string s && !string.IsNullOrEmpty(s))
            {
                var attr = prop.GetCustomAttribute<MaskedAttribute>()!;
                prop.SetValue(entity, _policy.Mask(s, attr.MaskType));
            }
        }

        return entity;
    }

    private static IEnumerable<PropertyInfo> MaskedProps(IReadOnlyEntityType entityType)
        => entityType.GetProperties().Select(p => p.PropertyInfo).OfType<PropertyInfo>()
            .Where(p => p.GetCustomAttribute<MaskedAttribute>() is not null);
}
