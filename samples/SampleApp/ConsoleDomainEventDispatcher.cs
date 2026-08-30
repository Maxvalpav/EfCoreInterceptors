using EfCore.Interceptors.Abstractions;
using SampleApp;

namespace SampleApp;

/// <summary>Prints dispatched domain events to the console.</summary>
public sealed class ConsoleDomainEventDispatcher(Action<IDomainEvent> sink) : IDomainEventDispatcher
{
    public void Dispatch(IEnumerable<IDomainEvent> domainEvents)
    {
        foreach (var e in domainEvents)
        {
            sink(e);
        }
    }

    public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        Dispatch(domainEvents);
        return Task.CompletedTask;
    }
}
