using System.Data.Common;
using System.Runtime.CompilerServices;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Outbox-style dispatch of domain events:
/// before saving it snapshots <see cref="IHasDomainEvents"/> aggregates into a per-context buffer,
/// and after a successful save publishes the buffered events via <see cref="IDomainEventDispatcher"/>
/// and clears them from the aggregates. If the save fails the events stay on their aggregates
/// and will be picked up by the next attempt.
/// </summary>
public class DomainEventsSaveChangesInterceptor(IDomainEventDispatcher? dispatcher = null) : SaveChangesInterceptor, IOrderedInterceptor, IDbTransactionInterceptor
{
    public int Order => 300;
    private static readonly ConditionalWeakTable<DbContext, PendingHolder> _pending = new();
    public static void Clear(DbContext context) => _pending.Remove(context);
    private sealed class PendingHolder(List<(IHasDomainEvents Aggregate, IReadOnlyList<IDomainEvent> Events)> snapshot)
    {
        public List<(IHasDomainEvents Aggregate, IReadOnlyList<IDomainEvent> Events)> Snapshot { get; } = snapshot;
    }

    private readonly IDomainEventDispatcher? _dispatcher = dispatcher;

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

        var snapshot = holder.Snapshot;
        var events = snapshot.SelectMany(s => s.Events).ToArray();

        if (_dispatcher is null || events.Length == 0)
        {
            _pending.Remove(context);
            foreach (var (aggregate, _) in snapshot)
            {
                aggregate.ClearDomainEvents();
            }

            return;
        }

        try
        {
            _dispatcher.Dispatch(events);
            _pending.Remove(context);
            foreach (var (aggregate, _) in snapshot)
            {
                aggregate.ClearDomainEvents();
            }
        }
        catch (Exception ex)
        {
            throw new DomainEventDispatchException("Domain event dispatch failed after a successful SaveChanges.", ex);
        }
        // Clear already done in Snapshot — no second clear needed
    }

    private async Task PublishAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null || !_pending.TryGetValue(context, out var holder))
        {
            return;
        }

        var snapshot = holder.Snapshot;
        var events = snapshot.SelectMany(s => s.Events).ToArray();

        if (_dispatcher is null || events.Length == 0)
        {
            _pending.Remove(context);
            return;
        }

        try
        {
            await _dispatcher.DispatchAsync(events, cancellationToken);
            _pending.Remove(context);
        }
        catch (Exception ex)
        {
            throw new DomainEventDispatchException("Domain event dispatch failed after a successful SaveChanges.", ex);
        }
    }
}

/// <summary>Dispatch failed after DB commit — changes were saved. Inspect InnerException.</summary>
public sealed class DomainEventDispatchException(string message, Exception inner) : InvalidOperationException(message, inner)
{
    public bool ChangesWereSaved => true;
}
