using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Materialization;

/// <summary>
/// Calls <see cref="IInitializable.OnLoaded"/> right after an entity is materialized —
/// a place to recompute transient state, wire up non-mapped helpers or refresh caches.
/// </summary>
public class InitializationMaterializationInterceptor : IMaterializationInterceptor
{
    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        if (entity is IInitializable initializable)
        {
            initializable.OnLoaded();
        }

        return entity;
    }
}
