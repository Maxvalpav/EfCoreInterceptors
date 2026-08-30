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
public class DomainEventsSaveChangesInterceptor(IDomainEventDispatcher? dispatcher = null) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, PendingHolder> _pending = new();
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
        Publish(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await PublishAsync(eventData.Context, cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        // Keep aggregate events intact for retry — only drop the pending snapshot.
        if (eventData.Context is not null)
        {
            _pending.Remove(eventData.Context);
        }

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            _pending.Remove(eventData.Context);
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
            // Keep pending + aggregate events for retry; do not clear.
            throw new InvalidOperationException("Domain event dispatch failed after a successful SaveChanges.", ex);
        }
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
            foreach (var (aggregate, _) in snapshot)
            {
                aggregate.ClearDomainEvents();
            }

            return;
        }

        try
        {
            await _dispatcher.DispatchAsync(events, cancellationToken);
            _pending.Remove(context);
            foreach (var (aggregate, _) in snapshot)
            {
                aggregate.ClearDomainEvents();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Domain event dispatch failed after a successful SaveChanges.", ex);
        }
    }
}
