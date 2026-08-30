using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.MediatR;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tests.Infrastructure;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace EfCore.Interceptors.Tests;

public class MediatRDomainEventDispatcherTests
{
    public record OrderShipped(int OrderId) : IDomainEvent
    {
        public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class CapturingHandler(List<OrderShipped> received)
        : INotificationHandler<DomainEventNotification<OrderShipped>>
    {
        public Task Handle(DomainEventNotification<OrderShipped> notification, CancellationToken ct)
        {
            received.Add(notification.DomainEvent);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Domain_events_flow_through_mediatr_pipeline()
    {
        var received = new List<OrderShipped>();

        var services = new ServiceCollection();
        services.AddSingleton(received);
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<MediatRDomainEventDispatcherTests>());
        services.AddMediatRDomainEventDispatcher();

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDomainEventDispatcher>();

        // Aggregate raises an event; interceptor drains it and calls the dispatcher.
        var kennel = new Kennel { Id = 1, Title = "M" };
        kennel.AddDomainEvent(new OrderShipped(42));

        dispatcher.Dispatch(new[] { (IDomainEvent)new OrderShipped(7) });

        Assert.Single(received);
        Assert.Equal(7, received[0].OrderId);
    }

    [Fact]
    public void End_to_end_via_SaveChangesInterceptor()
    {
        var received = new List<OrderShipped>();

        var services = new ServiceCollection();
        services.AddSingleton(received);
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<MediatRDomainEventDispatcherTests>());
        services.AddMediatRDomainEventDispatcher();

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDomainEventDispatcher>();

        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithDomainEvents(dispatcher)));

        db.Database.EnsureCreated();
        var kennel = new Kennel { Id = 9, Title = "Ship" };
        kennel.AddDomainEvent(new OrderShipped(99));
        db.Kennels.Add(kennel);
        db.SaveChanges();

        Assert.Single(received);
        Assert.Equal(99, received[0].OrderId);
        Assert.Empty(kennel.DomainEvents);
    }
}
