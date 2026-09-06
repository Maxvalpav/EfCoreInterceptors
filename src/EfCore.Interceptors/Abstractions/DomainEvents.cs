namespace EfCore.Interceptors.Abstractions;

/// <summary>Marker contract for domain events raised by aggregates.</summary>
public interface IDomainEvent
{
    /// <summary>UTC timestamp when the event was raised.</summary>
    DateTimeOffset OccurredAtUtc { get; }
}

/// <summary>
/// Optional event identity for outbox idempotency and partitioning: the same
/// (event type, aggregate, version) always maps to the same
/// <see cref="OutboxEventKeys.IdempotencyKeyFor(IDomainEvent)"/>, so a redelivered or
/// re-queued event is recognized by consumers, and all events of one aggregate share a
/// <see cref="OutboxEventKeys.PartitionKeyFor(IDomainEvent)"/> for ordered per-partition delivery.
/// </summary>
public interface IHasEventIdentity
{
    /// <summary>Aggregate instance id (order id, tenant-pet id, ...).</summary>
    string AggregateId { get; }

    /// <summary>Per-aggregate sequence number (optimistic-concurrency version, event number, ...).</summary>
    long Version { get; }
}

/// <summary>Deterministic outbox keys derived from domain events.</summary>
public static class OutboxEventKeys
{
    /// <summary>
    /// Idempotency key: stable for (event type, aggregate, version) when the event implements
    /// <see cref="IHasEventIdentity"/>, unique per instance otherwise. Consumers store handled
    /// keys and skip repeats for at-least-once delivery without double effects.
    /// </summary>
    public static Guid IdempotencyKeyFor(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent is IHasEventIdentity id && !string.IsNullOrEmpty(id.AggregateId))
        {
            var text = domainEvent.GetType().FullName + "|" + id.AggregateId + "|" + id.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
            return new Guid(hash[..16]);
        }
        return Guid.NewGuid();
    }

    /// <summary>
    /// Partition key: the aggregate id (all events of one aggregate stay ordered under parallel
    /// delivery), or the event type name when the event carries no identity.
    /// </summary>
    public static string PartitionKeyFor(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent is IHasEventIdentity id && !string.IsNullOrEmpty(id.AggregateId))
            return id.AggregateId;
        return domainEvent.GetType().FullName ?? domainEvent.GetType().Name;
    }
}

/// <summary>
/// Implemented by aggregate roots that collect domain events while they are being mutated.
/// <see cref="Saving.DomainEventsSaveChangesInterceptor"/> drains the queue before saving
/// and publishes it through an <see cref="IDomainEventDispatcher"/> after a successful save.
/// </summary>
public interface IHasDomainEvents
{
    /// <summary>Pending, not yet published events.</summary>
    IReadOnlyList<IDomainEvent> DomainEvents { get; }

    /// <summary>Queues an event.</summary>
    void AddDomainEvent(IDomainEvent domainEvent);

    /// <summary>Removes all queued events.</summary>
    void ClearDomainEvents();
}

/// <summary>Publishes drained domain events.</summary>
/// <remarks>
/// Sync <c>SaveChanges()</c> requires a working sync <see cref="Dispatch"/>: adapters that block
/// on async work (e.g. MediatR's sync-over-async wrapper) can starve the thread pool under load —
/// prefer <c>SaveChangesAsync()</c> in ASP.NET Core. The interceptor calls <see cref="Dispatch"/>
/// from <c>SavedChanges</c>/<c>TransactionCommitted</c> and <see cref="DispatchAsync"/> from the
/// async counterparts; both must be implemented.
/// </remarks>
public interface IDomainEventDispatcher
{
    /// <summary>Publishes events synchronously. Must not block on async work except via a completed path.</summary>
    void Dispatch(IEnumerable<IDomainEvent> domainEvents);

    /// <summary>Publishes events asynchronously.</summary>
    Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default);
}
