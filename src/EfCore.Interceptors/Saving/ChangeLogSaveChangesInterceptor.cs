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

        var now = _clock.GetUtcNow();
        var actor = _users.UserName;
        List<ChangeLogEntry>? logEntries = null;

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
            logEntries.Add(new ChangeLogEntry
            {
                EntityName = entry.Metadata.ClrType.Name,
                EntityKey = SerializeKey(entry),
                Action = entry.State.ToString(),
                ChangesJson = JsonSerializer.Serialize(changes, JsonOptions),
                Actor = actor,
                TimestampUtc = now
            });
        }

        if (logEntries is not null)
        {
            context.Set<ChangeLogEntry>().AddRange(logEntries);
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
        => JsonSerializer.Serialize(
            entry.Metadata.FindPrimaryKey()!.Properties
                .ToDictionary(p => p.Name, p => entry.Property(p.Name).CurrentValue),
            JsonOptions);
}
