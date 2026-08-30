using System.Text.Json;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Writes a detailed audit trail to the ChangeLogEntries table in the same transaction as the
/// business change: entity name, serialized primary key, action (Added/Modified/Deleted) and a
/// JSON diff of changed properties (old/new values).
/// Requirements: <c>modelBuilder.Entity&lt;ChangeLogEntry&gt;();</c> must be mapped.
/// </summary>
public class ChangeLogSaveChangesInterceptor(
    ICurrentUserProvider? currentUserProvider = null,
    TimeProvider? clock = null) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ICurrentUserProvider _users = currentUserProvider ?? StaticCurrentUserProvider.System;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DbContext, List<(EntityEntry Source, ChangeLogEntry Log)>> _pendingKeys = new();

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

    protected virtual void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        if (context.Model.FindEntityType(typeof(ChangeLogEntry)) is null)
        {
            // Fail fast before building diffs
            var hasAny = context.ChangeTracker.Entries()
                .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                    && e.Entity is not ChangeLogEntry and not OutboxMessage);
            if (hasAny)
            {
                throw new InvalidOperationException(
                    "ChangeLogEntry is not mapped. Call modelBuilder.Entity<ChangeLogEntry>() in OnModelCreating.");
            }

            return;
        }

        var now = _clock.GetUtcNow();
        var actor = _users.UserName;
        List<ChangeLogEntry>? logEntries = null;
        List<(EntityEntry Source, ChangeLogEntry Log)>? pendingAdded = null;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted) ||
                entry.Entity is ChangeLogEntry or OutboxMessage)
            {
                continue;
            }

            var changes = BuildDiff(entry);
            if (changes.Count == 0 && entry.State == EntityState.Modified)
            {
                continue;
            }

            logEntries ??= [];
            var log = new ChangeLogEntry
            {
                EntityName = entry.Metadata.ClrType.Name,
                EntityKey = SerializeKey(entry),
                Action = entry.State.ToString(),
                ChangesJson = JsonSerializer.Serialize(changes, JsonOptions),
                Actor = actor,
                TimestampUtc = now
            };
            logEntries.Add(log);

            if (entry.State == EntityState.Added)
            {
                pendingAdded ??= [];
                pendingAdded.Add((entry, log));
            }
        }

        if (logEntries is not null)
        {
            context.Set<ChangeLogEntry>().AddRange(logEntries);
            if (pendingAdded is not null && pendingAdded.Count > 0)
            {
                _pendingKeys[context] = pendingAdded;
            }
        }
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        PatchKeys(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        PatchKeys(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is not null) _pendingKeys.TryRemove(eventData.Context, out _);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) _pendingKeys.TryRemove(eventData.Context, out _);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void PatchKeys(DbContext? context)
    {
        if (context is null || !_pendingKeys.TryRemove(context, out var pending)) return;
        foreach (var (source, log) in pending)
        {
            // Re-serialize key after DB generated values (identity)
            log.EntityKey = SerializeKey(source);
        }
    }

    private static List<Dictionary<string, object?>> BuildDiff(EntityEntry entry)
        => entry.Properties
            .Where(p => entry.State != EntityState.Modified || p.IsModified)
            .Select(p => new Dictionary<string, object?>
            {
                ["property"] = p.Metadata.Name,
                ["old"] = entry.State == EntityState.Added ? null : p.OriginalValue,
                ["new"] = entry.State == EntityState.Deleted ? null : p.CurrentValue
            })
            .ToList();

    private static string SerializeKey(EntityEntry entry)
    {
        var pk = entry.Metadata.FindPrimaryKey();
        if (pk is null) return "{}";
        return JsonSerializer.Serialize(
            pk.Properties
                .ToDictionary(p => p.Name, p => entry.Property(p.Name).CurrentValue),
            JsonOptions);
    }
}
