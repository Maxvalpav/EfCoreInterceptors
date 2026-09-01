using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace EfCore.Interceptors;

/// <summary>
/// Safe bulk helpers that respect soft-delete, audit and tenant invariants (bulk-operations-gap L3).
/// Use these instead of raw <c>ExecuteDelete/ExecuteUpdate</c> on guarded entities.
/// </summary>
public static class BulkExtensions
{
    /// <summary>Bulk soft-delete: UPDATE IsDeleted=1, DeletedAtUtc, DeletedBy where predicate.</summary>
    public static async Task<int> ExecuteSoftDeleteAsync<T>(
        this IQueryable<T> query,
        ICurrentUserProvider? users = null,
        TimeProvider? clock = null,
        CancellationToken ct = default) where T : class, ISoftDeletableEntity
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var user = (users ?? StaticCurrentUserProvider.System).UserName;
        return await query.ExecuteUpdateAsync(s => s
            .SetProperty(e => e.IsDeleted, true)
            .SetProperty(e => e.DeletedAtUtc, now)
            .SetProperty(e => e.DeletedBy, user), ct).ConfigureAwait(false);
    }

    public static int ExecuteSoftDelete<T>(
        this IQueryable<T> query,
        ICurrentUserProvider? users = null,
        TimeProvider? clock = null) where T : class, ISoftDeletableEntity
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var user = (users ?? StaticCurrentUserProvider.System).UserName;
        return query.ExecuteUpdate(s => s
            .SetProperty(e => e.IsDeleted, true)
            .SetProperty(e => e.DeletedAtUtc, now)
            .SetProperty(e => e.DeletedBy, user));
    }

    /// <summary>Bulk restore: UPDATE IsDeleted=0 where predicate.</summary>
    public static Task<int> ExecuteRestoreAsync<T>(this IQueryable<T> query, CancellationToken ct = default) where T : class, ISoftDeletableEntity
        => query.ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDeleted, false).SetProperty(e => e.DeletedAtUtc, (DateTimeOffset?)null).SetProperty(e => e.DeletedBy, (string?)null), ct);

    /// <summary>Audited bulk update: applies SetProperty plus UpdatedAtUtc/UpdatedBy stamps.</summary>
#if NET10_0_OR_GREATER
    public static Task<int> ExecuteAuditedUpdateAsync<T>(
        this IQueryable<T> query,
        Action<UpdateSettersBuilder<T>> setAction,
        ICurrentUserProvider? users = null,
        TimeProvider? clock = null,
        CancellationToken ct = default) where T : class, IAuditableEntity
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var user = (users ?? StaticCurrentUserProvider.System).UserName;
        return query.ExecuteUpdateAsync(s => { setAction(s); s.SetProperty(e => e.UpdatedAtUtc, now); s.SetProperty(e => e.UpdatedBy, user); }, ct);
    }
#else
    public static Task<int> ExecuteAuditedUpdateAsync<T>(
        this IQueryable<T> query,
        Func<SetPropertyCalls<T>, SetPropertyCalls<T>> setPropertyCalls,
        ICurrentUserProvider? users = null,
        TimeProvider? clock = null,
        CancellationToken ct = default) where T : class, IAuditableEntity
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var user = (users ?? StaticCurrentUserProvider.System).UserName;
        return query.ExecuteUpdateAsync(s => setPropertyCalls(s).SetProperty(e => e.UpdatedAtUtc, now).SetProperty(e => e.UpdatedBy, user), ct);
    }
#endif
}
