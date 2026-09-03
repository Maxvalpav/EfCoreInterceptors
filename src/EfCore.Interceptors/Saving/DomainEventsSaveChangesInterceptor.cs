using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// What happens when in-process dispatch fails AFTER the data was committed (05.6).
/// <list type="bullet">
/// <item><see cref="Throw"/> — current behavior: the caller sees an exception although data is saved.</item>
/// <item><see cref="Log"/> — swallow + LogError + metric (for best-effort notifications).</item>
/// <item><see cref="RouteToOutbox"/> — persist the events as <see cref="OutboxMessage"/> rows
/// (requires the outbox entity mapped): turns in-process at-least-once into durable
/// at-least-once without changing domain code.</item>
/// </list>
/// </summary>
public enum DispatchFailurePolicy
{
    Throw,
    Log,
    RouteToOutbox
}

/// <summary>
/// Outbox-style dispatch of domain events:
/// before saving it snapshots <see cref="IHasDomainEvents"/> aggregates into a per-context buffer,
/// and after a successful save publishes the buffered events via <see cref="IDomainEventDispatcher"/>
/// and clears them from the aggregates. If the save fails the events stay on their aggregates
/// and will be picked up by the next attempt.
/// </summary>
public class DomainEventsSaveChangesInterceptor(
    IDomainEventDispatcher? dispatcher = null,
    DispatchFailurePolicy failurePolicy = DispatchFailurePolicy.Throw,
    ILoggerFactory? loggerFactory = null,
    TimeProvider? clock = null) : SaveChangesInterceptor, IOrderedInterceptor, IDbTransactionInterceptor
{
    public int Order => 300;
    private static readonly ConditionalWeakTable<DbContext, PendingHolder> _pending = new();
    public static void Clear(DbContext context) => _pending.Remove(context);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly Counter<long> DispatchFailed =
        SharedMeter.Meter.CreateCounter<long>("ef.domainevents.failed");
    private static readonly Counter<long> RoutedToOutbox =
        SharedMeter.Meter.CreateCounter<long>("ef.domainevents.routed_to_outbox");
    private sealed class PendingHolder(List<(IHasDomainEvents Aggregate, IReadOnlyList<IDomainEvent> Events)> snapshot)
    {
        public List<(IHasDomainEvents Aggregate, IReadOnlyList<IDomainEvent> Events)> Snapshot { get; } = snapshot;
    }

    private readonly IDomainEventDispatcher? _dispatcher = dispatcher;
    private readonly DispatchFailurePolicy _failurePolicy = failurePolicy;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.DomainEvents") ?? NullLogger.Instance;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Snapshot(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Snapshot(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        // Defer dispatch if external transaction is still open (logic-audit #5)
        if (eventData.Context?.Database.CurrentTransaction != null) return base.SavedChanges(eventData, result);
        Publish(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context?.Database.CurrentTransaction != null) return await base.SavedChangesAsync(eventData, result, cancellationToken);
        await PublishAsync(eventData.Context, cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    // IDbTransactionInterceptor — dispatch only on commit (at-least-once inside process)
    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Publish(eventData.Context);
    public async ValueTask TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await PublishAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
    }
    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData.Context is { } ctx && _pending.TryGetValue(ctx, out var holder))
        {
            _pending.Remove(ctx);
            foreach (var (agg, evts) in holder.Snapshot) foreach (var e in evts) agg.AddDomainEvent(e);
        }
    }
    public ValueTask TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        TransactionRolledBack(transaction, eventData);
        return ValueTask.CompletedTask;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } ctx && _pending.TryGetValue(ctx, out var holder))
        {
            _pending.Remove(ctx);
            foreach (var (agg, evts) in holder.Snapshot) foreach (var e in evts) agg.AddDomainEvent(e);
        }

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } ctx && _pending.TryGetValue(ctx, out var holder))
        {
            _pending.Remove(ctx);
            foreach (var (agg, evts) in holder.Snapshot) foreach (var e in evts) agg.AddDomainEvent(e);
        }

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void Snapshot(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        List<(IHasDomainEvents, IReadOnlyList<IDomainEvent>)>? snapshot = null;

        foreach (var aggregate in context.ChangeTracker.Entries<IHasDomainEvents>().Select(e => e.Entity))
        {
            if (aggregate.DomainEvents.Count == 0)
            {
                continue;
            }

            snapshot ??= [];
            snapshot.Add((aggregate, aggregate.DomainEvents.ToArray()));
        }

        if (snapshot is not null)
        {
            _pending.Remove(context);
            _pending.Add(context, new PendingHolder(snapshot));
            // Clear aggregates now — matches Outbox semantics, prevents duplicates on rollback
            foreach (var (agg, _) in snapshot) agg.ClearDomainEvents();
        }
    }

    private void Publish(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var holder))
        {
            return;
        }

        // Detach first: nested saves (RouteToOutbox policy) must not re-enter dispatch.
        _pending.Remove(context);
        var snapshot = holder.Snapshot;
        var events = snapshot.SelectMany(s => s.Events).ToArray();

        if (_dispatcher is null || events.Length == 0)
        {
            foreach (var (aggregate, _) in snapshot)
            {
                aggregate.ClearDomainEvents();
            }

            return;
        }

        try
        {
            _dispatcher.Dispatch(events);
            foreach (var (aggregate, _) in snapshot)
            {
                aggregate.ClearDomainEvents();
            }
        }
        catch (Exception ex)
        {
            HandleFailure(context, events, ex);
        }
        // Clear already done in Snapshot — no second clear needed
    }

    private async Task PublishAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null || !_pending.TryGetValue(context, out var holder))
        {
            return;
        }

        // Detach first: nested saves (RouteToOutbox policy) must not re-enter dispatch.
        _pending.Remove(context);
        var snapshot = holder.Snapshot;
        var events = snapshot.SelectMany(s => s.Events).ToArray();

        if (_dispatcher is null || events.Length == 0)
        {
            return;
        }

        try
        {
            await _dispatcher.DispatchAsync(events, cancellationToken);
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(context, events, ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandleFailure(DbContext? context, IDomainEvent[] events, Exception ex)
    {
        DispatchFailed.Add(events.Length);
        switch (_failurePolicy)
        {
            case DispatchFailurePolicy.Log:
                _logger.LogError(ex,
                    "Domain event dispatch failed after a successful SaveChanges ({Count} events swallowed by policy).",
                    events.Length);
                if (context is not null) _pending.Remove(context);
                break;
            case DispatchFailurePolicy.RouteToOutbox:
                RouteToOutbox(context, events, ex);
                if (context is not null) _pending.Remove(context);
                break;
            default:
                throw new DomainEventDispatchException("Domain event dispatch failed after a successful SaveChanges.", ex);
        }
    }

    private async Task HandleFailureAsync(
        DbContext? context, IDomainEvent[] events, Exception ex, CancellationToken cancellationToken)
    {
        DispatchFailed.Add(events.Length);
        switch (_failurePolicy)
        {
            case DispatchFailurePolicy.Log:
                _logger.LogError(ex,
                    "Domain event dispatch failed after a successful SaveChanges ({Count} events swallowed by policy).",
                    events.Length);
                if (context is not null) _pending.Remove(context);
                break;
            case DispatchFailurePolicy.RouteToOutbox:
                await RouteToOutboxAsync(context, events, ex, cancellationToken).ConfigureAwait(false);
                if (context is not null) _pending.Remove(context);
                break;
            default:
                throw new DomainEventDispatchException("Domain event dispatch failed after a successful SaveChanges.", ex);
        }
    }

    private void RouteToOutbox(DbContext? context, IDomainEvent[] events, Exception ex)
    {
        if (context is null || context.Model.FindEntityType(typeof(OutboxMessage)) is null)
            throw new DomainEventDispatchException(
                "RouteToOutbox requires modelBuilder.Entity<OutboxMessage>().", ex);
        var now = _clock.GetUtcNow();
        foreach (var domainEvent in events)
        {
            context.Set<OutboxMessage>().Add(new OutboxMessage
            {
                Type = domainEvent.GetType().FullName ?? domainEvent.GetType().Name,
                PayloadJson = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
                OccurredAtUtc = domainEvent.OccurredAtUtc == default ? now : domainEvent.OccurredAtUtc,
                Error = $"Routed from failed in-process dispatch: {ex.Message[..Math.Min(500, ex.Message.Length)]}"
            });
        }
        context.SaveChanges();
        RoutedToOutbox.Add(events.Length);
        _logger.LogWarning(ex,
            "Domain event dispatch failed; {Count} events routed to outbox for durable delivery.",
            events.Length);
    }

    private async Task RouteToOutboxAsync(
        DbContext? context, IDomainEvent[] events, Exception ex, CancellationToken cancellationToken)
    {
        if (context is null || context.Model.FindEntityType(typeof(OutboxMessage)) is null)
            throw new DomainEventDispatchException(
                "RouteToOutbox requires modelBuilder.Entity<OutboxMessage>().", ex);
        var now = _clock.GetUtcNow();
        foreach (var domainEvent in events)
        {
            context.Set<OutboxMessage>().Add(new OutboxMessage
            {
                Type = domainEvent.GetType().FullName ?? domainEvent.GetType().Name,
                PayloadJson = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
                OccurredAtUtc = domainEvent.OccurredAtUtc == default ? now : domainEvent.OccurredAtUtc,
                Error = $"Routed from failed in-process dispatch: {ex.Message[..Math.Min(500, ex.Message.Length)]}"
            });
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        RoutedToOutbox.Add(events.Length);
        _logger.LogWarning(ex,
            "Domain event dispatch failed; {Count} events routed to outbox for durable delivery.",
            events.Length);
    }
}

/// <summary>Dispatch failed after DB commit — changes were saved. Inspect InnerException.</summary>
public sealed class DomainEventDispatchException(string message, Exception inner) : InvalidOperationException(message, inner)
{
    public bool ChangesWereSaved => true;
}
