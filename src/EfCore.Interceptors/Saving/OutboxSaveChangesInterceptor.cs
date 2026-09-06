using System.Runtime.CompilerServices;
using System.Text.Json;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Atomic outbox: serializes <see cref="IHasDomainEvents"/> events into <see cref="OutboxMessage"/>
/// rows inserted in the SAME transaction as the business change. A background worker (not included)
/// reads OutboxMessages, delivers them and stamps ProcessedAtUtc.
/// Requirements: <c>modelBuilder.Entity&lt;OutboxMessage&gt;();</c> must be mapped.
/// </summary>
/// <remarks>
/// Combination with <see cref="DomainEventsSaveChangesInterceptor"/> (Order 300) is supported:
/// this interceptor (Order 200) only <i>copies</i> events into outbox rows and never clears the
/// aggregates, so the domain-events interceptor still sees the full set, publishes it in-process
/// and performs the single clearing. Standalone (without domain events) the aggregates are cleared
/// after a successful save (immediately, or on transaction commit when a transaction is open),
/// so a retry or a second <c>SaveChanges</c> inside the same transaction never queues duplicates
/// (already-queued event references are skipped). On save failure only the failed batch is detached;
/// on transaction rollback the holder is dropped while the aggregates keep their events.
/// </remarks>
public class OutboxSaveChangesInterceptor(TimeProvider? timeProvider = null)
    : SaveChangesInterceptor, IOrderedInterceptor, IDbTransactionInterceptor
{
    public int Order => 200;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    // Per-context buffers isolated per interceptor instance (two instances must never
    // overwrite each other's batches). Clearing is shared: Clear(context) drops everyone.
    private static readonly ConditionalWeakTable<DbContext, Dictionary<object, PendingHolder>> _pending = new();
    public static void Clear(DbContext context) => _pending.Remove(context);

    private sealed class Batch(
        List<(IHasDomainEvents Aggregate, IReadOnlyList<IDomainEvent> Events)> snapshot,
        List<OutboxMessage> rows)
    {
        public List<(IHasDomainEvents Aggregate, IReadOnlyList<IDomainEvent> Events)> Snapshot { get; } = snapshot;
        public List<OutboxMessage> Rows { get; } = rows;
    }

    private sealed class PendingHolder
    {
        public List<Batch> Batches { get; } = [];
        public HashSet<IDomainEvent> Queued { get; } = new(ReferenceEqualityComparer.Instance);
    }

    // Batch appended by the most recent SnapshotAndQueue on this async flow, so SaveChangesFailed
    // detaches exactly the failed save instead of an older batch from the same transaction.
    private readonly AsyncLocal<(DbContext? Context, Batch? Batch)> _lastBatch = new();

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
        SavedChangesCore(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        SavedChangesCore(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private void SavedChangesCore(DbContext? context)
    {
        _lastBatch.Value = (null, null);
        if (context is null) return;
        // Inside an explicit transaction the rows commit later: keep the holder for
        // duplicate suppression across several SaveChanges; clear on commit.
        if (context.Database.CurrentTransaction != null) return;
        ClearAggregates(context, dropHolder: true);
    }

    public void TransactionCommitted(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData)
    {
        _lastBatch.Value = (null, null);
        if (eventData.Context is not null) ClearAggregates(eventData.Context, dropHolder: true);
    }

    // NOTE: Task (not ValueTask) — must match IDbTransactionInterceptor exactly, otherwise
    // commit/rollback handling silently goes dead on the async path.
    public Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        TransactionCommitted(transaction, eventData);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData)
    {
        _lastBatch.Value = (null, null);
        // Outbox rows are rolled back with the transaction; aggregates were never cleared,
        // so their events stay queued for the next attempt — just drop the holder.
        if (eventData.Context is not null) TakeHolder(eventData.Context);
    }

    public Task TransactionRolledBackAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        TransactionRolledBack(transaction, eventData);
        return Task.CompletedTask;
    }

    private void SnapshotAndQueue(DbContext? context)
    {
        _lastBatch.Value = (null, null);
        if (context is null)
        {
            return;
        }

        PendingHolder holder;
        lock (_pending)
        {
            if (!_pending.TryGetValue(context, out var map))
            {
                map = new Dictionary<object, PendingHolder>();
                _pending.Add(context, map);
            }
            if (!map.TryGetValue(this, out holder!))
            {
                holder = new PendingHolder();
                map[this] = holder;
            }
        }

        // Queue only events not queued before (second SaveChanges inside one transaction
        // must not duplicate rows for events that are still sitting on the aggregates).
        List<(IHasDomainEvents, IReadOnlyList<IDomainEvent>)>? fresh = null;
        lock (holder)
        {
            foreach (var aggregate in context.ChangeTracker.Entries<IHasDomainEvents>().Select(e => e.Entity))
            {
                if (aggregate.DomainEvents.Count == 0) continue;
                var unqueued = aggregate.DomainEvents.Where(e => !holder.Queued.Contains(e)).ToArray();
                if (unqueued.Length == 0) continue;
                fresh ??= [];
                fresh.Add((aggregate, unqueued));
            }
        }

        if (fresh is null)
        {
            return;
        }

        if (context.Model.FindEntityType(typeof(OutboxMessage)) is null)
        {
            throw new InvalidOperationException(
                "OutboxMessage is not mapped. Call modelBuilder.Entity<OutboxMessage>() in OnModelCreating.");
        }

        var now = _timeProvider.GetUtcNow();
        var rows = new List<OutboxMessage>(fresh.Sum(f => f.Item2.Count));
        foreach (var (aggregate, events) in fresh)
        {
            foreach (var domainEvent in events)
            {
                var row = new OutboxMessage
                {
                    Type = domainEvent.GetType().FullName ?? domainEvent.GetType().Name,
                    PayloadJson = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
                    OccurredAtUtc = domainEvent.OccurredAtUtc == default ? now : domainEvent.OccurredAtUtc,
                    IdempotencyKey = OutboxEventKeys.IdempotencyKeyFor(domainEvent),
                    PartitionKey = OutboxEventKeys.PartitionKeyFor(domainEvent)
                };
                context.Set<OutboxMessage>().Add(row);
                rows.Add(row);
            }
        }

        var batch = new Batch(fresh, rows);
        lock (holder)
        {
            holder.Batches.Add(batch);
            foreach (var (_, events) in fresh)
                foreach (var e in events)
                    holder.Queued.Add(e);
        }
        _lastBatch.Value = (context, batch);
    }

    /// <summary>
    /// Clears queued events from the aggregates (only events this interceptor queued — events added
    /// afterwards by user code stay for the next save) and optionally drops the holder.
    /// </summary>
    private void ClearAggregates(DbContext context, bool dropHolder)
    {
        PendingHolder? holder;
        lock (_pending)
        {
            if (!_pending.TryGetValue(context, out var map) || !map.TryGetValue(this, out holder))
                return;
            if (dropHolder)
            {
                map.Remove(this);
                if (map.Count == 0) _pending.Remove(context);
            }
        }
        if (holder is null) return;
        lock (holder)
        {
            foreach (var batch in holder.Batches)
            {
                foreach (var (aggregate, events) in batch.Snapshot)
                {
                    var current = aggregate.DomainEvents;
                    if (current.Count == 0) continue;
                    // Clear only when nothing new arrived after the snapshot; otherwise the new
                    // events stay queued and will be picked up by the next save.
                    if (current.All(e => holder.Queued.Contains(e)))
                        aggregate.ClearDomainEvents();
                }
            }
            if (dropHolder)
            {
                holder.Batches.Clear();
                holder.Queued.Clear();
            }
        }
    }

    private PendingHolder? TakeHolder(DbContext context)
    {
        lock (_pending)
        {
            if (!_pending.TryGetValue(context, out var map)) return null;
            if (!map.Remove(this, out var holder)) return null;
            if (map.Count == 0) _pending.Remove(context);
            return holder;
        }
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context)
        {
            RollbackLastBatch(context);
        }

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            RollbackLastBatch(context);
        }

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <summary>
    /// Detaches outbox rows of the failed save and forgets its events (they stay on the aggregates
    /// — this interceptor never clears before success — so a retry re-queues them exactly once).
    /// </summary>
    private void RollbackLastBatch(DbContext context)
    {
        var (batchContext, batch) = _lastBatch.Value;
        _lastBatch.Value = (null, null);
        if (batch is null || !ReferenceEquals(batchContext, context)) return;

        foreach (var row in batch.Rows)
        {
            try
            {
                var entry = context.Entry(row);
                if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
            }
            catch (InvalidOperationException) { /* row already detached / context disposed */ }
        }

        lock (_pending)
        {
            if (!_pending.TryGetValue(context, out var map) || !map.TryGetValue(this, out var holder))
                return;
            lock (holder)
            {
                holder.Batches.Remove(batch);
                // Rebuild the queued set: an event may appear in several batches only if it was
                // re-added after a previous failure — recompute from remaining batches.
                holder.Queued.Clear();
                foreach (var b in holder.Batches)
                    foreach (var (_, events) in b.Snapshot)
                        foreach (var e in events)
                            holder.Queued.Add(e);
                if (holder.Batches.Count == 0 && context.Database.CurrentTransaction is null)
                {
                    map.Remove(this);
                    if (map.Count == 0) _pending.Remove(context);
                }
            }
        }
    }
}
