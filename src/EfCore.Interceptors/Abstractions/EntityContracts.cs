namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Implemented by entities that want automatic creation/update audit stamps
/// maintained by <see cref="Saving.AuditSaveChangesInterceptor"/>.
/// All properties must be mapped in the model.
/// </summary>
public interface IAuditableEntity
{
    /// <summary>UTC timestamp when the row was first persisted.</summary>
    DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Name of the principal that created the row.</summary>
    string? CreatedBy { get; set; }

    /// <summary>UTC timestamp of the last modification. Null until first update.</summary>
    DateTimeOffset? UpdatedAtUtc { get; set; }

    /// <summary>Name of the principal that performed the last modification.</summary>
    string? UpdatedBy { get; set; }
}

/// <summary>
/// Implemented by entities deleted logically instead of physically.
/// Pair with <see cref="Saving.SoftDeleteSaveChangesInterceptor"/> and a global
/// query filter (<c>HasQueryFilter(e =&gt; !e.IsDeleted)</c>) so deleted rows stay invisible.
/// </summary>
public interface ISoftDeletableEntity
{
    /// <summary>Logical delete flag.</summary>
    bool IsDeleted { get; set; }

    /// <summary>UTC timestamp of deletion.</summary>
    DateTimeOffset? DeletedAtUtc { get; set; }

    /// <summary>Name of the principal that deleted the row.</summary>
    string? DeletedBy { get; set; }
}

/// <summary>Implemented by entities that record when they were last materialized.</summary>
public interface ILoadTimestamped
{
    /// <summary>UTC timestamp assigned right after the entity is materialized.</summary>
    DateTimeOffset? LoadedAtUtc { get; set; }
}

/// <summary>
/// Implemented by entities with an application-managed version counter for optimistic
/// concurrency on providers without native rowversion (SQLite, MySQL).
/// <see cref="Saving.VersionIncrementSaveChangesInterceptor"/> increments the counter on update;
/// declare it as a concurrency token (<c>.Property(v =&gt; v.Version).IsConcurrencyToken()</c>)
/// so stale writes fail.
/// </summary>
public interface IVersionedEntity
{
    /// <summary>Incremented on every update.</summary>
    long Version { get; set; }
}

/// <summary>
/// Adapter point for external validation libraries (FluentValidation, etc.) consumed by
/// <see cref="Saving.CustomValidationSaveChangesInterceptor"/> without taking a dependency.
/// </summary>
public interface IEntityValidator
{
    /// <summary>Returns violation messages for the entity; empty = valid.</summary>
    IEnumerable<string> Validate(object entity);
}
