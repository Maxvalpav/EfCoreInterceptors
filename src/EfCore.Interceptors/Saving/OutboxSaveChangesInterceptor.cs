using System.Runtime.CompilerServices;
using System.Text.Json;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Atomic outbox: drains <see cref="IHasDomainEvents"/> aggregates before saving, serializes each
/// event into an <see cref="OutboxMessage"/> row inserted in the SAME transaction as the business
/// change, and clears the aggregates. If the save fails the events are restored to their aggregates.
/// A background worker (not included) reads OutboxMessages, delivers them and stamps ProcessedAtUtc.
/// Requirements: <c>modelBuilder.Entity&lt;OutboxMessage&gt;();</c> must be mapped.
/// </summary>
public class OutboxSaveChangesInterceptor(TimeProvider? timeProvider = null) : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => 200;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private static readonly ConditionalWeakTable<DbContext, PendingHolder> _pending = new();
    public static void Clear(DbContext context) => _pending.Remove(context);
    private sealed class PendingHolder(List<(IHasDomainEvents Aggregate, IReadOnlyList<IDomainEvent> Events)> snapshot)
    {
        public List<(IHasDomainEvents Aggregate, IReadOnlyList<IDomainEvent> Events)> Snapshot { get; } = snapshot;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        SnapshotAndQueue(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        SnapshotAndQueue(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is not null)
        {
            _pending.Remove(eventData.Context);
        }

        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            _pending.Remove(eventData.Context);
        }

        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private void SnapshotAndQueue(DbContext? context)
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

        if (snapshot is null)
        {
            return;
        }

        if (context.Model.FindEntityType(typeof(OutboxMessage)) is null)
        {
            throw new InvalidOperationException(
                "OutboxMessage is not mapped. Call modelBuilder.Entity<OutboxMessage>() in OnModelCreating.");
        }

        var now = _timeProvider.GetUtcNow();

        foreach (var (aggregate, events) in snapshot)
        {
            foreach (var domainEvent in events)
            {
                context.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    Type = domainEvent.GetType().FullName ?? domainEvent.GetType().Name,
                    PayloadJson = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
                    OccurredAtUtc = domainEvent.OccurredAtUtc == default ? now : domainEvent.OccurredAtUtc
                });
            }

            // Events are now durably queued in this transaction — safe to clear.
            aggregate.ClearDomainEvents();
        }

        _pending.Remove(context);
        _pending.Add(context, new PendingHolder(snapshot));
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context)
        {
            // Detach OutboxMessage Added entries to avoid duplicates on retry
            foreach (var e in context.ChangeTracker.Entries<OutboxMessage>().Where(e => e.State == EntityState.Added).ToList())
                e.State = EntityState.Detached;
            if (_pending.TryGetValue(context, out var holder))
            {
                _pending.Remove(context);
                foreach (var (aggregate, events) in holder.Snapshot)
                    foreach (var domainEvent in events)
                        aggregate.AddDomainEvent(domainEvent);
            }
        }

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            foreach (var e in context.ChangeTracker.Entries<OutboxMessage>().Where(e => e.State == EntityState.Added).ToList())
                e.State = EntityState.Detached;
            if (_pending.TryGetValue(context, out var holder))
            {
                _pending.Remove(context);
                foreach (var (aggregate, events) in holder.Snapshot)
                    foreach (var domainEvent in events)
                        aggregate.AddDomainEvent(domainEvent);
            }
        }

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }
}
