using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Materialization;

/// <summary>
/// Stamps <see cref="ILoadTimestamped.LoadedAtUtc"/> on entities as they are materialized
/// from database queries — useful to know how stale an in-memory instance is.
/// </summary>
public class LoadStampingMaterializationInterceptor(TimeProvider? clock = null) : IMaterializationInterceptor
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        if (entity is ILoadTimestamped stamped)
        {
            stamped.LoadedAtUtc = _clock.GetUtcNow().UtcDateTime;
        }

        return entity;
    }
}
