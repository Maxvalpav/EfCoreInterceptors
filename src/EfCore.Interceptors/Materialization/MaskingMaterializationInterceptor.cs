using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<Type, (PropertyInfo Prop, MaskedAttribute Attr)[]> Cache = new();

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        var props = Cache.GetOrAdd(materializationData.EntityType.ClrType, static clrType =>
        {
            // Build from runtime type to avoid pinning IEntityType/IModel
            return clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Prop: p, Attr: p.GetCustomAttribute<MaskedAttribute>()))
                .Where(t => t.Attr is not null)
                .Select(t => (t.Prop, t.Attr!))
                .ToArray();
        });

        foreach (var (prop, attr) in props)
        {
            if (prop.GetValue(entity) is string s && !string.IsNullOrEmpty(s))
            {
                prop.SetValue(entity, _policy.Mask(s, attr.MaskType));
            }
        }

        return entity;
    }
}
