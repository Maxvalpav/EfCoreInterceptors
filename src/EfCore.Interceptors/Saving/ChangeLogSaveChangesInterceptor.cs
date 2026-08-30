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
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, PendingHolder> _pendingKeys = new();
    private sealed class PendingHolder(List<(EntityEntry Source, ChangeLogEntry Log)> pending) { public List<(EntityEntry Source, ChangeLogEntry Log)> Pending { get; } = pending; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DbContext, bool> _isPatching = new();

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
            // Avoid recursion when patching
            if (_isPatching.TryGetValue(context, out var patching) && patching) return;
            context.Set<ChangeLogEntry>().AddRange(logEntries);
            if (pendingAdded is not null && pendingAdded.Count > 0)
            {
                _pendingKeys.Remove(context);
                _pendingKeys.Add(context, new PendingHolder(pendingAdded));
            }
        }
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        PatchKeys(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await PatchKeysAsync(eventData.Context);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is not null) _pendingKeys.Remove(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) _pendingKeys.Remove(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void PatchKeys(DbContext? context)
    {
        if (context is null || !_pendingKeys.TryGetValue(context, out var holder)) return;
        _pendingKeys.Remove(context);
        var pending = holder.Pending;
        var needsSecondSave = false;
        foreach (var (source, log) in pending)
        {
            var corrected = SerializeKey(source);
            if (log.EntityKey != corrected)
            {
                log.EntityKey = corrected;
                needsSecondSave = true;
            }
        }

        // Persist corrected keys with a second save in a new transaction (audit trail must be accurate)
        if (needsSecondSave && context is not null)
        {
            try
            {
                _isPatching[context] = true;
                context.SaveChanges();
            }
            finally
            {
                _isPatching.TryRemove(context, out _);
            }
        }
    }

    private async Task PatchKeysAsync(DbContext? context)
    {
        if (context is null || !_pendingKeys.TryGetValue(context, out var holder)) return;
        _pendingKeys.Remove(context);
        var pending = holder.Pending;
        var needsSecondSave = false;
        foreach (var (source, log) in pending)
        {
            var corrected = SerializeKey(source);
            if (log.EntityKey != corrected)
            {
                log.EntityKey = corrected;
                needsSecondSave = true;
            }
        }

        if (needsSecondSave && context is not null)
        {
            try
            {
                _isPatching[context] = true;
                await context.SaveChangesAsync();
            }
            finally
            {
                _isPatching.TryRemove(context, out _);
            }
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
