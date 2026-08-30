namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Marker for entity types whose rows must never be deleted (e.g. financial records).
/// Enforced by <see cref="Saving.DeleteGuardSaveChangesInterceptor"/>.
/// </summary>
public interface IProtectedEntity
{
}

/// <summary>
/// Marker for append-only entity types: inserts are allowed, but modifications and deletes
/// are rejected by <see cref="Saving.ImmutableEntityGuardSaveChangesInterceptor"/>
/// (e.g. audit history, posted ledger entries).
/// </summary>
public interface IImmutableEntity
{
}

/// <summary>
/// Hook called once right after an entity is materialized from a query —
/// recompute transient/cached state, wire non-mapped helpers, etc.
/// Invoked by <see cref="Materialization.InitializationMaterializationInterceptor"/>.
/// </summary>
public interface IInitializable
{
    void OnLoaded();
}
