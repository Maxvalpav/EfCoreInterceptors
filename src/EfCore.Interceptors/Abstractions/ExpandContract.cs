using System.Collections.Concurrent;
using System.Reflection;
using EfCore.Interceptors.Abstractions;

namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Blue-green column migration (03.16, expand-contract): put on the NEW property to declare
/// the OLD one it replaces. The dual-write interceptor mirrors new→old on save; the fallback
/// materializer serves old→new for not-yet-backfilled rows; the progress counter shows
/// how many rows still lack the new column.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MigratedFromAttribute(string oldPropertyName) : Attribute
{
    public string OldPropertyName { get; } = oldPropertyName;
}

internal static class ExpandContractCache
{
    internal static readonly ConcurrentDictionary<Type, (PropertyInfo New, PropertyInfo Old)[]> Map = new();

    internal static (PropertyInfo New, PropertyInfo Old)[] For(Type clrType)
        => Map.GetOrAdd(clrType, static t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (New: p, Attr: p.GetCustomAttribute<MigratedFromAttribute>()))
                .Where(x => x.Attr is not null && x.New.SetMethod is not null)
                .Select(x => (x.New, Old: t.GetProperty(x.Attr!.OldPropertyName,
                    BindingFlags.Public | BindingFlags.Instance)!))
                .Where(x => x.Old is not null && x.Old.PropertyType == x.New.PropertyType && x.Old.SetMethod is not null)
                .Select(x => (x.New, x.Old!))
                .ToArray());
}
