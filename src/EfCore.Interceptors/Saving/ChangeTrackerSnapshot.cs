using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// One scan per SaveChanges — caches ChangeTracker.Entries() snapshot per DbContext per save (performance-audit #1).
/// First interceptor's Get triggers one scan, subsequent Gets reuse same list. Cleared on SavedChanges.
/// </summary>
internal static class ChangeTrackerSnapshot
{
    private static readonly ConditionalWeakTable<DbContext, List<EntityEntry>> _cache = new();
    private static readonly AsyncLocal<HashSet<DbContext>?> _active = new();

    public static IEnumerable<EntityEntry> GetAll(DbContext context)
    {
        if (_cache.TryGetValue(context, out var cached)) return cached;
        var all = context.ChangeTracker.Entries().ToList();
        _cache.Remove(context);
        _cache.Add(context, all);
        var set = _active.Value ??= [];
        set.Add(context);
        return all;
    }

    public static IEnumerable<EntityEntry> Get<T>(DbContext context) where T : class
        => GetAll(context).Where(e => e.Entity is T);

    public static void End(DbContext context)
    {
        _cache.Remove(context);
        if (_active.Value != null) _active.Value.Remove(context);
    }

    /// <summary>Ensures End is called even when SavingChanges throws — use via try/finally in interceptors.</summary>
    public static void BeginScope(DbContext context) => GetAll(context);

    public static bool IsActive(DbContext context) => _cache.TryGetValue(context, out _);
}
