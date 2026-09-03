using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors;

/// <summary>
/// Point-in-time reads over system-versioned history (03.1): reconstructs entities as of
/// a timestamp from <see cref="TemporalRecord"/> snapshots. Ticks comparisons run
/// server-side (translatable on every provider); deserialization is client-side.
/// Best for audits, legal holds and replay investigations — not for hot paths.
/// </summary>
public static class TemporalQuery
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Entities of type <typeparamref name="T"/> as they were at <paramref name="timestamp"/>.
    /// Rows that did not exist yet — or were already deleted — are absent.
    /// Rows whose snapshot no longer deserializes (schema drift) are skipped.
    /// </summary>
    public static async Task<List<T>> AsOfAsync<T>(
        DbContext db,
        DateTimeOffset timestamp,
        int? take = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var ticks = timestamp.UtcTicks;
        var entityName = typeof(T).FullName ?? typeof(T).Name;
        var query = db.Set<TemporalRecord>()
            .Where(r => r.EntityName == entityName && r.TicksFrom <= ticks
                && (r.TicksTo == null || r.TicksTo > ticks))
            .OrderBy(r => r.Id);
        var versions = take.HasValue
            ? await query.Take(Math.Max(1, take.Value)).ToListAsync(cancellationToken).ConfigureAwait(false)
            : await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<T>(versions.Count);
        foreach (var version in versions)
        {
            if (TryRestore<T>(version.SnapshotJson) is { } entity)
                result.Add(entity);
        }
        return result;
    }

    /// <summary>Full version timeline for one row (ordered oldest → newest).</summary>
    public static async Task<List<TemporalRecord>> GetHistoryAsync<T>(
        DbContext db,
        string entityKey,
        CancellationToken cancellationToken = default) where T : class
    {
        var entityName = typeof(T).FullName ?? typeof(T).Name;
        return await db.Set<TemporalRecord>()
            .Where(r => r.EntityName == entityName && r.EntityKey == entityKey)
            .OrderBy(r => r.TicksFrom)
            .ThenBy(r => r.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rebuilds one entity from its snapshot JSON (tolerates schema drift per property).</summary>
    public static T? Restore<T>(string snapshotJson) where T : class
        => TryRestore<T>(snapshotJson);

    private static T? TryRestore<T>(string snapshotJson) where T : class
    {
        Dictionary<string, JsonElement>? values;
        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(snapshotJson, JsonOptions);
        }
        catch (JsonException) { return default; }
        if (values is null) return default;

        object instance;
        try { instance = RuntimeHelpers.GetUninitializedObject(typeof(T)); }
        catch { return default; }

        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is not null)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var (name, element) in values)
        {
            if (!props.TryGetValue(name, out var pi)) continue; // dropped column — skip
            try
            {
                var value = element.ValueKind == JsonValueKind.Null
                    ? null
                    : JsonSerializer.Deserialize(element.GetRawText(), pi.PropertyType, JsonOptions);
                pi.SetValue(instance, value);
            }
            catch (JsonException) { /* renamed/retyped column — keep default */ }
        }
        return (T)instance;
    }
}
