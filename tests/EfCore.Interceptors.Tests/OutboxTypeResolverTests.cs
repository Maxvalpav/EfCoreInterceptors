using System.Reflection;
using System.Text.Json;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace EfCore.Interceptors.Tests;

/// <summary>
/// Security S-6: outbox type resolution is a deserialization boundary.
/// A tampered <c>OutboxMessage.Type</c> must never smuggle an arbitrary CLR type
/// into the deserializer — unlisted/non-event types fail closed.
/// </summary>
public sealed class OutboxTypeResolverTests
{
    public sealed record OrderPaid(string OrderId) : IDomainEvent
    {
        public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class PaidHandler : IOutboxEventHandler<OrderPaid>
    {
        public int Seen;
        public ValueTask HandleAsync(OrderPaid evt, OutboxMessage message, CancellationToken ct)
        {
            Seen++;
            return ValueTask.CompletedTask;
        }
    }

    private static DispatchingOutboxMessageHandler Dispatcher(
        IServiceProvider services, IOutboxTypeResolver? resolver = null)
        => new(services, resolver);

    [Fact]
    public async Task Non_event_type_is_rejected_before_deserialization()
    {
        var dispatcher = Dispatcher(new ServiceCollection().BuildServiceProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.HandleAsync(new OutboxMessage
        {
            Id = 1,
            Type = typeof(string).FullName!, // resolves fine — but is not a domain event
            PayloadJson = "\"pwned\"",
            OccurredAtUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Unknown_type_is_rejected()
    {
        var dispatcher = Dispatcher(new ServiceCollection().BuildServiceProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.HandleAsync(new OutboxMessage
        {
            Id = 2,
            Type = "Totally.NotARealType, NoSuchAssembly",
            PayloadJson = "{}",
            OccurredAtUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Allow_list_resolves_listed_types_end_to_end()
    {
        var handler = new PaidHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxEventHandler<OrderPaid>>(handler);
        var provider = services.BuildServiceProvider();
        var dispatcher = Dispatcher(provider,
            new AllowListOutboxTypeResolver([typeof(OrderPaid)]));

        var evt = new OrderPaid("ord-7");
        await dispatcher.HandleAsync(new OutboxMessage
        {
            Id = 3,
            Type = typeof(OrderPaid).FullName!,
            PayloadJson = JsonSerializer.Serialize(evt),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.Equal(1, handler.Seen);
    }

    [Fact]
    public async Task Allow_list_rejects_unlisted_types()
    {
        var dispatcher = Dispatcher(new ServiceCollection().BuildServiceProvider(),
            new AllowListOutboxTypeResolver([typeof(OrderPaid)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.HandleAsync(new OutboxMessage
        {
            Id = 4,
            Type = typeof(string).FullName!,
            PayloadJson = "\"pwned\"",
            OccurredAtUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public void Allow_list_assembly_mode_resolves_same_assembly_rejects_foreign()
    {
        var resolver = new AllowListOutboxTypeResolver(typeof(OrderPaid).Assembly);
        Assert.Equal(typeof(OrderPaid), resolver.Resolve(typeof(OrderPaid).FullName!));
        Assert.Null(resolver.Resolve(typeof(string).FullName!));
        Assert.Null(resolver.Resolve("Totally.NotARealType"));
    }

    [Fact]
    public void Allow_list_type_mode_resolves_by_full_name_only()
    {
        var resolver = new AllowListOutboxTypeResolver([typeof(OrderPaid)]);
        Assert.Equal(typeof(OrderPaid), resolver.Resolve(typeof(OrderPaid).FullName!));
        Assert.Null(resolver.Resolve("OrderPaid")); // short name is not allow-listed
    }
}
