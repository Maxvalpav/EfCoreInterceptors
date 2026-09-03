namespace EfCore.Interceptors.Entities;

/// <summary>
/// One system-versioned snapshot (03.1, SCD Type 2): the row was valid from
/// <see cref="TicksFrom"/> (inclusive) until <see cref="TicksTo"/> (exclusive, null = open).
/// Ticks (not DateTimeOffset) are stored so range predicates translate on every provider,
/// including SQLite. Map it (<c>modelBuilder.Entity&lt;TemporalRecord&gt;()</c>) and add an
/// index on <c>(EntityName, EntityKey, TicksFrom)</c> for history scans.
/// </summary>
public class TemporalRecord
{
    public long Id { get; set; }

    /// <summary>Full CLR type name of the versioned entity.</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>Serialized primary key of the versioned row.</summary>
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>Full property snapshot as JSON object (name → value).</summary>
    public string SnapshotJson { get; set; } = "{}";

    /// <summary>Validity start, <see cref="DateTimeOffset.UtcTicks"/>.</summary>
    public long TicksFrom { get; set; }

    /// <summary>Validity end (exclusive), null while the version is current.</summary>
    public long? TicksTo { get; set; }

    /// <summary>Added / Modified / Deleted.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Actor resolved via ICurrentUserProvider.</summary>
    public string? Actor { get; set; }
}
