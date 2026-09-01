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
    TimeProvider? clock = null) : SaveChangesInterceptor, IOrderedInterceptor
{
    public int Order => 100;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ICurrentUserProvider _users = currentUserProvider ?? StaticCurrentUserProvider.System;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, PendingHolder> _pendingKeys = new();
    private sealed class PendingHolder(List<(EntityEntry Source, ChangeLogEntry Log)> pending) { public List<(EntityEntry Source, ChangeLogEntry Log)> Pending { get; } = pending; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, PatchGuard> _isPatching = new();
    private sealed class PatchGuard { public bool Value; }
    public static void Clear(DbContext context) { _pendingKeys.Remove(context); _isPatching.Remove(context); }

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
            if (_isPatching.TryGetValue(context, out var g) && g.Value) return;
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
        await PatchKeysAsync(eventData.Context).ConfigureAwait(false);
        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
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

        // Persist corrected keys - must stay in same transaction if one is active, otherwise wrap atomically
        if (needsSecondSave && context is not null)
        {
            var guard = new PatchGuard { Value = true };
            _isPatching.Remove(context);
            _isPatching.Add(context, guard);
            try
            {
                if (context.Database.CurrentTransaction is not null)
                {
                    context.SaveChanges();
                }
                else
                {
                    using var tx = context.Database.BeginTransaction();
                    context.SaveChanges();
                    tx.Commit();
                }
            }
            finally
            {
                _isPatching.Remove(context);
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
            var guard = new PatchGuard { Value = true };
            _isPatching.Remove(context);
            _isPatching.Add(context, guard);
            try
            {
                if (context.Database.CurrentTransaction is not null)
                {
                    await context.SaveChangesAsync().ConfigureAwait(false);
                }
                else
                {
                    await using var tx = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
                    await context.SaveChangesAsync().ConfigureAwait(false);
                    await tx.CommitAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _isPatching.Remove(context);
            }
        }
    }

    private static List<Dictionary<string, object?>> BuildDiff(EntityEntry entry)
    {
        // Include owned entities' properties that are stored as part of same table
        var diff = new List<Dictionary<string, object?>>();
        foreach (var p in entry.Properties)
        {
            if (entry.State == EntityState.Modified && !p.IsModified) continue;
            diff.Add(new Dictionary<string, object?>
            {
                ["property"] = p.Metadata.Name,
                ["old"] = entry.State == EntityState.Added ? null : p.OriginalValue,
                ["new"] = entry.State == EntityState.Deleted ? null : p.CurrentValue
            });
        }
        // Complex types (EF8+) recursive — provider-matrix 2.4, logic-audit #6
        foreach (var complex in entry.ComplexProperties)
            AddComplexDiff(complex, entry.State, diff);
        // Owned value-objects (e.g. Owned<T>) are separate entries that would otherwise be missed
        foreach (var nav in entry.References)
        {
            var target = nav.TargetEntry;
            if (target is not null && target.Metadata.IsOwned() && target.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                diff.AddRange(BuildDiff(target));
            }
        }
        // JSON columns: single property blob — still log as-is; caller can mask via [Sensitive] later
        return diff;
    }

    private static void AddComplexDiff(ComplexPropertyEntry complex, EntityState state, List<Dictionary<string, object?>> diff)
    {
        foreach (var p in complex.Properties)
        {
            if (state == EntityState.Modified && !p.IsModified) continue;
            diff.Add(new Dictionary<string, object?>
            {
                ["property"] = $"{complex.Metadata.Name}.{p.Metadata.Name}",
                ["old"] = state == EntityState.Added ? null : p.OriginalValue,
                ["new"] = state == EntityState.Deleted ? null : p.CurrentValue
            });
        }
        foreach (var nested in complex.ComplexProperties)
            AddComplexDiff(nested, state, diff);
    }

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
