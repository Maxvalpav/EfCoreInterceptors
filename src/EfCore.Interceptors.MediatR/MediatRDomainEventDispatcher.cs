using EfCore.Interceptors.Abstractions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace EfCore.Interceptors.MediatR;

/// <summary>
/// Wraps an arbitrary <see cref="IDomainEvent"/> into a MediatR <see cref="INotification"/>,
/// so handlers subscribe to the concrete event type while the dispatcher stays type-less.
/// </summary>
public sealed class DomainEventNotification<TDomainEvent>(TDomainEvent domainEvent) : INotification
    where TDomainEvent : IDomainEvent
{
    /// <summary>The original domain event.</summary>
    public TDomainEvent DomainEvent { get; } = domainEvent;
}

/// <summary>
/// <see cref="IDomainEventDispatcher"/> implementation backed by MediatR:
/// every drained event is published through <c>IMediator.Publish</c>, so normal
/// <c>INotificationHandler&lt;DomainEventNotification&lt;TEvent&gt;&gt;</c> handlers
/// (and pipeline behaviors) receive it.
/// </summary>
public sealed class MediatRDomainEventDispatcher(IMediator mediator) : IDomainEventDispatcher
{
    private readonly IMediator _mediator = mediator;

    public void Dispatch(IEnumerable<IDomainEvent> domainEvents)
        => DispatchAsync(domainEvents).GetAwaiter().GetResult();

    public async Task DispatchAsync(
        IEnumerable<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        foreach (var domainEvent in domainEvents)
        {
            var notification = Wrap(domainEvent);
            await _mediator.Publish(notification, cancellationToken);
        }
    }

    private static INotification Wrap(IDomainEvent domainEvent)
        => (INotification)Activator.CreateInstance(
            typeof(DomainEventNotification<>).MakeGenericType(domainEvent.GetType()),
            domainEvent)!;
}

public static class MediatRDispatcherExtensions
{
    /// <summary>
    /// Registers the MediatR-backed <see cref="IDomainEventDispatcher"/> (singleton),
    /// ready to be passed into <c>WithDomainEvents(dispatcher)</c>.
    /// Requires MediatR services to be registered (<c>AddMediatR(...)</c>).
    /// </summary>
    public static IServiceCollection AddMediatRDomainEventDispatcher(this IServiceCollection services)
        => services.AddSingleton<Abstractions.IDomainEventDispatcher>(
            sp => new MediatRDomainEventDispatcher(sp.GetRequiredService<IMediator>()));
}
