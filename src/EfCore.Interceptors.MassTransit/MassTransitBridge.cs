using System.Text.Json;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.Observability;
using MassTransit;

namespace EfCore.Interceptors.MassTransitAdapter;

/// <summary>
/// Publishes drained domain events to the MassTransit bus (03.10):
/// <c>.UseEfInterceptors(s => s.WithDomainEvents(new MassTransitDomainEventDispatcher(publishEndpoint)))</c>.
/// </summary>
public sealed class MassTransitDomainEventDispatcher(IPublishEndpoint publishEndpoint) : IDomainEventDispatcher
{
    public void Dispatch(IEnumerable<IDomainEvent> domainEvents)
    {
        foreach (var evt in domainEvents)
            publishEndpoint.Publish(evt, evt.GetType(), CancellationToken.None)
                .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public async Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        foreach (var evt in domainEvents)
            await publishEndpoint.Publish(evt, evt.GetType(), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Delivers outbox rows to the MassTransit bus (03.10): resolves each
/// <see cref="OutboxMessage.Type"/> via <see cref="IOutboxTypeResolver"/>,
/// deserializes the payload and publishes it. Failures propagate to the
/// processor's retry / dead-letter path.
/// </summary>
public sealed class MassTransitOutboxMessageHandler(
    IPublishEndpoint publishEndpoint,
    IOutboxTypeResolver? typeResolver = null,
    JsonSerializerOptions? jsonOptions = null) : IOutboxMessageHandler
{
    private readonly IOutboxTypeResolver _resolver = typeResolver ?? new DefaultOutboxTypeResolver();
    private readonly JsonSerializerOptions _json = jsonOptions ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    public async ValueTask HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var eventType = _resolver.Resolve(message.Type)
            ?? throw new InvalidOperationException(
                $"Outbox message {message.Id}: cannot resolve event type '{message.Type}'.");
        var evt = JsonSerializer.Deserialize(message.PayloadJson, eventType, _json)
            ?? throw new InvalidOperationException(
                $"Outbox message {message.Id}: payload deserialized to null for '{message.Type}'.");
        await publishEndpoint.Publish(evt, eventType, cancellationToken).ConfigureAwait(false);
    }
}
