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

/// <summary>
/// What <see cref="Saving.SoftDeleteSaveChangesInterceptor"/> does when the same
/// <c>SaveChanges</c> also hard-deletes entities without <see cref="ISoftDeletableEntity"/>
/// (e.g. soft-deleting an <c>Order</c> together with a hard-deletable <c>TempFile</c>).
/// </summary>
public enum SoftDeleteOrphanPolicy
{
    /// <summary>Throw <c>SoftDeleteCascadeException</c> (safe default: no silent mixed deletes).</summary>
    Throw,

    /// <summary>Allow unrelated hard deletes in the same unit of work.</summary>
    AllowHardDelete,
}
