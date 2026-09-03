using System.Text.Json.Serialization;

namespace EfCore.Interceptors.Entities;

/// <summary>
/// Persisted audit-trail row describing one entity change (insert/update/delete).
/// Map it in your model (e.g. <c>modelBuilder.Entity&lt;ChangeLogEntry&gt;();</c>) so
/// <see cref="Saving.ChangeLogSaveChangesInterceptor"/> can append rows in the same transaction.
/// </summary>
public class ChangeLogEntry
{
    public long Id { get; set; }

    /// <summary>CLR name of the changed entity type.</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>Serialized primary key of the changed row.</summary>
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>Added / Modified / Deleted.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>JSON array of { property, oldValue, newValue } for the changed columns.</summary>
    public string ChangesJson { get; set; } = "[]";

    /// <summary>Actor resolved via ICurrentUserProvider.</summary>
    public string? Actor { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }
}

/// <summary>
/// Atomic outbox row for an integration/domain event, written in the same transaction as the
/// business change. A background processor delivers rows and stamps <see cref="ProcessedAtUtc"/>.
/// Map it in your model (e.g. <c>modelBuilder.Entity&lt;OutboxMessage&gt;();</c>).
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }

    /// <summary>Full type name of the event.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>JSON-serialized event payload.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; set; }

    public DateTimeOffset? ProcessedAtUtc { get; set; }

    /// <summary>Claim lock for multi-instance delivery (null = not locked).</summary>
    public DateTimeOffset? LockedUntilUtc { get; set; }

    /// <summary>
    /// Unique claim token of the worker that owns the current lock.
    /// Replaces timestamp-equality claim (05.1): two instances computing the same
    /// <c>LockedUntilUtc</c> no longer select each other's batch.
    /// </summary>
    public Guid? ClaimToken { get; set; }

    /// <summary>
    /// When set, the message exceeded max delivery attempts and is parked in the
    /// dead-letter queue. Excluded from polling (05.2).
    /// </summary>
    public DateTimeOffset? DeadLetteredAtUtc { get; set; }

    /// <summary>Delivery attempt count.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Last delivery error (if any).</summary>
    public string? Error { get; set; }
}
