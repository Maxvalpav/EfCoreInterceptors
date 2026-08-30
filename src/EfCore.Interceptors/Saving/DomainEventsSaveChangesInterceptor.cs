using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<DbContext, List<(IHasDomainEvents Aggregate, IReadOnlyList<IDomainEvent> Events)>> _pending = new();
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
        Publish(eventData.Context, asyncDispatch: false);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Publish(eventData.Context, asyncDispatch: true, cancellationToken);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
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
            _pending[context] = snapshot;
        }
    }

    private void Publish(DbContext? context, bool asyncDispatch, CancellationToken cancellationToken = default)
    {
        if (context is null || !_pending.TryRemove(context, out var snapshot))
        {
            return;
        }

        // The database work succeeded: events are considered "committed", clear them.
        foreach (var (aggregate, _) in snapshot)
        {
            aggregate.ClearDomainEvents();
        }

        var events = snapshot.SelectMany(s => s.Events).ToArray();

        if (_dispatcher is null || events.Length == 0)
        {
            return;
        }

        try
        {
            if (asyncDispatch)
            {
                _dispatcher.DispatchAsync(events, cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                _dispatcher.Dispatch(events);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Domain event dispatch failed after a successful SaveChanges.", ex);
        }
    }
}
