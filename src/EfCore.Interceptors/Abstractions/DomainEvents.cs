namespace EfCore.Interceptors.Abstractions;

/// <summary>Marker contract for domain events raised by aggregates.</summary>
public interface IDomainEvent
{
    /// <summary>UTC timestamp when the event was raised.</summary>
    DateTimeOffset OccurredAtUtc { get; }
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
public interface IDomainEventDispatcher
{
    /// <summary>Publishes events synchronously.</summary>
    void Dispatch(IEnumerable<IDomainEvent> domainEvents);

    /// <summary>Publishes events asynchronously.</summary>
    Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default);
}
