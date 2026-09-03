using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Observability;

/// <summary>
/// Typed outbox handler: implement once per event type instead of writing a
/// manual type-switch in <see cref="IOutboxMessageHandler"/> (05.5).
/// </summary>
/// <typeparam name="TEvent">Domain event type (serialized as <see cref="OutboxMessage.PayloadJson"/>).</typeparam>
public interface IOutboxEventHandler<in TEvent>
{
    ValueTask HandleAsync(TEvent evt, OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves an <see cref="OutboxMessage.Type"/> string back to a CLR type.
/// The default scans loaded assemblies once and caches the result; pass a custom
/// implementation (or explicit map) when event types live in unloadable contexts.
/// </summary>
public interface IOutboxTypeResolver
{
    Type? Resolve(string typeName);
}

/// <summary>Default resolver: <c>Type.GetType</c> first, then a cached scan of loaded assemblies.</summary>
public sealed class DefaultOutboxTypeResolver : IOutboxTypeResolver
{
    private readonly ConcurrentDictionary<string, Type?> _cache = new(StringComparer.Ordinal);

    public Type? Resolve(string typeName) => _cache.GetOrAdd(typeName, static name =>
    {
        var direct = Type.GetType(name, throwOnError: false);
        if (direct is not null) return direct;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? found;
            try { found = asm.GetType(name, throwOnError: false); }
            catch { continue; }
            if (found is not null) return found;
        }
        return null;
    });
}

/// <summary>
/// <see cref="IOutboxMessageHandler"/> that routes each message to the matching
/// <see cref="IOutboxEventHandler{TEvent}"/> from DI (built once, invoked via cached
/// delegates — no per-message reflection). Deserialize via
/// <see cref="JsonSerializer"/> by default; supply <c>deserializer</c> to plug
/// source-generated contexts (AOT-friendly) instead.
/// </summary>
public sealed class DispatchingOutboxMessageHandler : IOutboxMessageHandler
{
    private readonly IServiceProvider _services;
    private readonly IOutboxTypeResolver _resolver;
    private readonly JsonSerializerOptions _json;
    private readonly Func<string, Type, object?>? _deserializer;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Type, Func<object, OutboxMessage, CancellationToken, ValueTask>> _invokers = new();

    public DispatchingOutboxMessageHandler(
        IServiceProvider services,
        IOutboxTypeResolver? typeResolver = null,
        JsonSerializerOptions? jsonOptions = null,
        Func<string, Type, object?>? deserializer = null,
        ILoggerFactory? loggerFactory = null)
    {
        _services = services;
        _resolver = typeResolver ?? new DefaultOutboxTypeResolver();
        _json = jsonOptions ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        _deserializer = deserializer;
        _logger = loggerFactory?.CreateLogger("EfCore.Interceptors.Outbox") ?? NullLogger.Instance;
    }

    public async ValueTask HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var eventType = _resolver.Resolve(message.Type)
            ?? throw new InvalidOperationException(
                $"Outbox message {message.Id}: cannot resolve event type '{message.Type}'. " +
                "Register IOutboxTypeResolver or use assembly-qualified type names.");
        var evt = (_deserializer is not null
                ? _deserializer(message.PayloadJson, eventType)
                : JsonSerializer.Deserialize(message.PayloadJson, eventType, _json))
            ?? throw new InvalidOperationException(
                $"Outbox message {message.Id}: payload deserialized to null for '{message.Type}'.");
        var invoker = _invokers.GetOrAdd(eventType, BuildInvoker);
        await invoker(evt, message, cancellationToken).ConfigureAwait(false);
    }

    private Func<object, OutboxMessage, CancellationToken, ValueTask> BuildInvoker(Type eventType)
    {
        var handlerType = typeof(IOutboxEventHandler<>).MakeGenericType(eventType);
        var method = handlerType.GetMethod(nameof(IOutboxEventHandler<IDomainEvent>.HandleAsync))!;
        return (evt, message, ct) =>
        {
            var handler = _services.GetService(handlerType)
                ?? throw new InvalidOperationException(
                    $"No {handlerType.Name} registered for event '{eventType.FullName}'.");
            try
            {
                return (ValueTask)method.Invoke(handler, [evt, message, ct])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        };
    }
}

public static class OutboxDispatchingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the dispatching outbox handler (05.5). Register your
    /// <c>IOutboxEventHandler&lt;T&gt;</c> implementations alongside it.
    /// </summary>
    public static IServiceCollection AddOutboxDispatcher(
        this IServiceCollection services,
        IOutboxTypeResolver? typeResolver = null,
        JsonSerializerOptions? jsonOptions = null,
        Func<string, Type, object?>? deserializer = null)
        => services.AddScoped<IOutboxMessageHandler>(sp => new DispatchingOutboxMessageHandler(
            sp, typeResolver ?? sp.GetService<IOutboxTypeResolver>(), jsonOptions, deserializer,
            sp.GetService<ILoggerFactory>()));
}
