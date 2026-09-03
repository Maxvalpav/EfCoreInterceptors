using System.Runtime.CompilerServices;
using System.Text.Json;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// System-versioned history, SCD Type 2 (03.1): tracked inserts/updates/deletes append
/// version rows in the same transaction; the previously open version is closed
/// (<c>TicksTo = now</c>). Tracked = <see cref="TemporalAttribute"/> or explicit types.
/// Primary keys are assumed immutable (standard for versioned rows).
/// Requirements: <c>modelBuilder.Entity&lt;TemporalRecord&gt;()</c> must be mapped.
/// </summary>
public class TemporalSaveChangesInterceptor(
    ICurrentUserProvider? currentUserProvider = null,
    TimeProvider? clock = null,
    params Type[] trackedTypes) : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => 110;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ICurrentUserProvider _users = currentUserProvider ?? StaticCurrentUserProvider.System;
    private readonly HashSet<Type> _tracked = new(trackedTypes);
    private static readonly ConditionalWeakTable<DbContext, List<(EntityEntry Source, TemporalRecord Record)>> _pendingAdded = new();
    public static void Clear(DbContext context) => _pendingAdded.Remove(context);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        PatchAddedKeys(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await PatchAddedKeysAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is not null) _pendingAdded.Remove(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) _pendingAdded.Remove(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private bool IsTracked(Type clrType)
        => _tracked.Contains(clrType)
            || clrType.GetCustomAttributes(typeof(TemporalAttribute), inherit: true).Length > 0;

    private static bool IsInfrastructure(object entity)
        => entity is TemporalRecord or ChangeLogEntry or OutboxMessage;

    private void Collect(DbContext? context)
    {
        if (context is null) return;
        if (context.Model.FindEntityType(typeof(TemporalRecord)) is null)
        {
            var hasAny = context.ChangeTracker.Entries()
                .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                    && !IsInfrastructure(e.Entity) && IsTracked(e.Entity.GetType()));
            if (hasAny)
                throw new InvalidOperationException(
                    "TemporalRecord is not mapped. Call modelBuilder.Entity<TemporalRecord>() in OnModelCreating.");
            return;
        }

        var now = _clock.GetUtcNow();
        var ticks = now.UtcTicks;
        var actor = _users.UserName;

        // Group Modified/Deleted keys per entity name to close previous versions in one query each.
        var closings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var pendingAdded = new List<(EntityEntry, TemporalRecord)>();

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)
                || IsInfrastructure(entry.Entity) || !IsTracked(entry.Entity.GetType()))
                continue;

            var entityName = entry.Entity.GetType().FullName ?? entry.Entity.GetType().Name;
            var key = SerializeKey(entry);
            var snapshot = CaptureSnapshot(entry);

            if (entry.State == EntityState.Deleted)
            {
                closings.GetOrAdd(entityName, _ => []).Add(key);
                // Tombstone: valid for a single instant so AsOf(t) finds nothing afterwards.
                context.Set<TemporalRecord>().Add(new TemporalRecord
                {
                    EntityName = entityName, EntityKey = key, SnapshotJson = snapshot,
                    TicksFrom = ticks, TicksTo = ticks, Action = "Deleted", Actor = actor
                });
                continue;
            }

            if (entry.State == EntityState.Modified)
                closings.GetOrAdd(entityName, _ => []).Add(key);

            var record = new TemporalRecord
            {
                EntityName = entityName, EntityKey = key, SnapshotJson = snapshot,
                TicksFrom = ticks, TicksTo = null,
                Action = entry.State.ToString(), Actor = actor
            };
            context.Set<TemporalRecord>().Add(record);
            if (entry.State == EntityState.Added)
                pendingAdded.Add((entry, record));
        }

        // Close previously open versions (same transaction, same save).
        foreach (var (entityName, keys) in closings)
        {
            var distinct = keys.Distinct().ToList();
            var open = context.Set<TemporalRecord>()
                .Where(r => r.EntityName == entityName && r.TicksTo == null && distinct.Contains(r.EntityKey))
                .ToList();
            foreach (var row in open) row.TicksTo = ticks;
        }

        if (pendingAdded.Count > 0)
        {
            _pendingAdded.Remove(context);
            _pendingAdded.Add(context, pendingAdded);
        }
    }

    private void PatchAddedKeys(DbContext? context)
    {
        if (context is null || !_pendingAdded.TryGetValue(context, out var pending)) return;
        _pendingAdded.Remove(context);
        var dirty = false;
        foreach (var (source, record) in pending)
        {
            var key = SerializeKey(source);
            if (record.EntityKey != key) { record.EntityKey = key; dirty = true; }
            var snapshot = CaptureSnapshot(source);
            if (record.SnapshotJson != snapshot) { record.SnapshotJson = snapshot; dirty = true; }
        }
        if (dirty)
        {
            if (context.Database.CurrentTransaction is not null) context.SaveChanges();
            else { using var tx = context.Database.BeginTransaction(); context.SaveChanges(); tx.Commit(); }
        }
    }

    private async Task PatchAddedKeysAsync(DbContext? context, CancellationToken ct)
    {
        if (context is null || !_pendingAdded.TryGetValue(context, out var pending)) return;
        _pendingAdded.Remove(context);
        var dirty = false;
        foreach (var (source, record) in pending)
        {
            var key = SerializeKey(source);
            if (record.EntityKey != key) { record.EntityKey = key; dirty = true; }
            var snapshot = CaptureSnapshot(source);
            if (record.SnapshotJson != snapshot) { record.SnapshotJson = snapshot; dirty = true; }
        }
        if (dirty)
        {
            if (context.Database.CurrentTransaction is not null)
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
            else
            {
                await using var tx = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private static string SerializeKey(EntityEntry entry)
    {
        var pk = entry.Metadata.FindPrimaryKey();
        if (pk is null) return "{}";
        return JsonSerializer.Serialize(
            pk.Properties.ToDictionary(p => p.Name, p => entry.Property(p.Name).CurrentValue),
            JsonOptions);
    }

    private static string CaptureSnapshot(EntityEntry entry)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in entry.Properties)
            values[p.Metadata.Name] = entry.State == EntityState.Deleted ? p.OriginalValue : p.CurrentValue;
        return JsonSerializer.Serialize(values, JsonOptions);
    }
}

file static class DictionaryExtensions
{
    internal static TValue GetOrAdd<TKey, TValue>(
        this Dictionary<TKey, TValue> map, TKey key, Func<TKey, TValue> factory) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var value))
        {
            value = factory(key);
            map[key] = value;
        }
        return value;
    }
}
