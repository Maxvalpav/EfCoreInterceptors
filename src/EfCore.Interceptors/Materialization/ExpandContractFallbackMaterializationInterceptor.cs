using System.Diagnostics.Metrics;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Observability;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Materialization;

/// <summary>
/// Expand-contract read fallback (03.16): when the NEW column still holds its default
/// (row not backfilled yet), serve the OLD value instead and count
/// <c>ef.expandcontract.fallbacks</c>. Remove with the old column once the progress
/// counter reaches zero.
/// </summary>
public class ExpandContractFallbackMaterializationInterceptor : IMaterializationInterceptor
{
    private static readonly Counter<long> Fallbacks =
        SharedMeter.Meter.CreateCounter<long>("ef.expandcontract.fallbacks");

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        foreach (var (neu, old) in ExpandContractCache.For(entity.GetType()))
        {
            if (!IsDefault(neu.GetValue(entity), neu.PropertyType)) continue;
            var oldValue = old.GetValue(entity);
            if (IsDefault(oldValue, old.PropertyType)) continue;
            neu.SetValue(entity, oldValue);
            Fallbacks.Add(1);
        }
        return entity;
    }

    private static bool IsDefault(object? value, Type type)
        => value is null || (type.IsValueType && Equals(value, Activator.CreateInstance(type)));
}
