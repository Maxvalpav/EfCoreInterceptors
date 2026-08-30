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
        if (eventData.Context is not null)
        {
            _pending.TryRemove(eventData.Context, out _);
        }

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            _pending.TryRemove(eventData.Context, out _);
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
            _pending[context] = snapshot;
        }
    }

    private void Publish(DbContext? context)
    {
        if (context is null || !_pending.TryRemove(context, out var snapshot))
        {
            return;
        }

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
            _dispatcher.Dispatch(events);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Domain event dispatch failed after a successful SaveChanges.", ex);
        }
    }

    private async Task PublishAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null || !_pending.TryRemove(context, out var snapshot))
        {
            return;
        }

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
            await _dispatcher.DispatchAsync(events, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Domain event dispatch failed after a successful SaveChanges.", ex);
        }
    }
}
