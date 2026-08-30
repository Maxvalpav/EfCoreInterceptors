using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Observability;

/// <summary>
/// Counts materialized entities: counter <c>ef.materialization.entities</c>.
/// A sudden spike is a classic signature of an accidental cartesian explosion or
/// missing pagination.
/// </summary>
public class MaterializationMetricsInterceptor : IMaterializationInterceptor
{
    private const string MeterName = "EfCore.Interceptors";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _entities;

    public MaterializationMetricsInterceptor()
        => _entities = _meter.CreateCounter<long>("ef.materialization.entities");

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        _entities.Add(1, new KeyValuePair<string, object?>(
            "entity", materializationData.EntityType.ClrType.Name));
        return entity;
    }
}
