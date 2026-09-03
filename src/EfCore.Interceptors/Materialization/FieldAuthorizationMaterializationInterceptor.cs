using System.Collections.Concurrent;
using System.Reflection;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Materialization;

/// <summary>
/// Field-level authorization on read (03.3): properties marked
/// <see cref="RequiresRoleAttribute"/> the current principal is not in are reset to
/// their default value right after materialization. Pair with the SaveChanges guard.
/// </summary>
public class FieldAuthorizationMaterializationInterceptor(IRoleProvider roles) : IMaterializationInterceptor
{
    private readonly IRoleProvider _roles = roles;
    private static readonly ConcurrentDictionary<Type, (PropertyInfo Prop, string[] Roles)[]> Cache = new();

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        var props = Cache.GetOrAdd(materializationData.EntityType.ClrType, static clrType =>
            clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Prop: p, Attr: p.GetCustomAttribute<RequiresRoleAttribute>()))
                .Where(t => t.Attr is not null && t.Prop.SetMethod is not null)
                .Select(t => (t.Prop, t.Attr!.Roles))
                .ToArray());

        foreach (var (prop, required) in props)
        {
            if (!required.Any(_roles.IsInRole))
                prop.SetValue(entity, prop.PropertyType.IsValueType
                    ? Activator.CreateInstance(prop.PropertyType)
                    : null);
        }

        return entity;
    }
}
