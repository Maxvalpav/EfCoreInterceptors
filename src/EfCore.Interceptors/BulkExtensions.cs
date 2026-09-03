using System.Linq.Expressions;
using System.Reflection;
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
    /// <summary>
    /// Bulk update of a single <c>[Encrypted]</c> property (05.9, 07.1): the plaintext is
    /// encrypted on the client with the same <paramref name="encryptor"/> before it reaches
    /// <c>ExecuteUpdate</c>, so ciphertext lands in the column — never plaintext.
    /// Fail-closed: throws when the property lacks <see cref="EncryptedAttribute"/> or the
    /// value already looks like ciphertext (double-encryption guard).
    /// Note: per-row AAD binding is impossible in bulk (PKs vary per row); the value is
    /// encrypted without AAD, exactly like the SaveChanges path with default settings.
    /// </summary>
    public static Task<int> ExecuteEncryptedUpdateAsync<T>(
        this IQueryable<T> query,
        Expression<Func<T, string?>> property,
        string? plaintext,
        IPropertyValueEncryptor encryptor,
        CancellationToken ct = default) where T : class
    {
        var cipher = EncryptBulkValue<T>(property, plaintext, encryptor);
#if NET10_0_OR_GREATER
        return query.ExecuteUpdateAsync(s => { s.SetProperty(property, cipher); }, ct);
#else
        var compiled = property.Compile();
        return query.ExecuteUpdateAsync(s => s.SetProperty(compiled, cipher), ct);
#endif
    }

    /// <summary>Sync variant of <see cref="ExecuteEncryptedUpdateAsync{T}"/>.</summary>
    public static int ExecuteEncryptedUpdate<T>(
        this IQueryable<T> query,
        Expression<Func<T, string?>> property,
        string? plaintext,
        IPropertyValueEncryptor encryptor) where T : class
    {
        var cipher = EncryptBulkValue<T>(property, plaintext, encryptor);
#if NET10_0_OR_GREATER
        return query.ExecuteUpdate(s => { s.SetProperty(property, cipher); });
#else
        return query.ExecuteUpdate(s => s.SetProperty(property.Compile(), cipher));
#endif
    }

    /// <summary>
    /// Encrypted bulk update with audit stamps for <see cref="IAuditableEntity"/>:
    /// ciphertext + <c>UpdatedAtUtc/UpdatedBy</c> in one statement.
    /// </summary>
    public static Task<int> ExecuteEncryptedAuditedUpdateAsync<T>(
        this IQueryable<T> query,
        Expression<Func<T, string?>> property,
        string? plaintext,
        IPropertyValueEncryptor encryptor,
        ICurrentUserProvider? users = null,
        TimeProvider? clock = null,
        CancellationToken ct = default) where T : class, IAuditableEntity
    {
        var cipher = EncryptBulkValue<T>(property, plaintext, encryptor);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var user = (users ?? StaticCurrentUserProvider.System).UserName;
#if NET10_0_OR_GREATER
        return query.ExecuteUpdateAsync(s => { s.SetProperty(property, cipher); s.SetProperty(e => e.UpdatedAtUtc, now); s.SetProperty(e => e.UpdatedBy, user); }, ct);
#else
        var compiledAudited = property.Compile();
        return query.ExecuteUpdateAsync(s => s
            .SetProperty(compiledAudited, cipher)
            .SetProperty(e => e.UpdatedAtUtc, now)
            .SetProperty(e => e.UpdatedBy, user), ct);
#endif
    }

    private static string? EncryptBulkValue<T>(
        Expression<Func<T, string?>> property,
        string? plaintext,
        IPropertyValueEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(encryptor);
        var name = property.Body is MemberExpression m ? m.Member.Name
            : throw new ArgumentException("Property expression must be a simple member access.", nameof(property));
        var pi = typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Property '{typeof(T).Name}.{name}' not found.");
        if (pi.GetCustomAttribute<EncryptedAttribute>() is null)
            throw new InvalidOperationException(
                $"Bulk-encrypted update refused: '{typeof(T).Name}.{name}' lacks [Encrypted]. " +
                "Use ExecuteAuditedUpdateAsync for plain properties — this guard prevents accidental plaintext writes to encrypted columns and vice versa.");
        if (plaintext is not null && encryptor.IsEncrypted(plaintext))
            throw new InvalidOperationException(
                $"Bulk-encrypted update refused: value for '{typeof(T).Name}.{name}' already looks like ciphertext (double-encryption guard).");
        return encryptor.Encrypt(plaintext);
    }

    /// <summary>
    /// Key-rotation migration (07.2): re-encrypts one <c>[Encrypted]</c> property from
    /// <paramref name="oldEncryptor"/> to <paramref name="newEncryptor"/> (e.g. v1 →
    /// key-ring v2) in tracking batches. Run as a background maintenance job and monitor
    /// the returned counters; rows that fail to decrypt with the old key are counted as
    /// skipped (when <paramref name="skipFailures"/> is true) rather than aborting the run.
    /// The query must be tracking (no <c>AsNoTracking</c>) — fail-closed otherwise.
    /// </summary>
    /// <returns>(migrated rows, skipped rows).</returns>
    public static async Task<(int Migrated, int Skipped)> ReEncryptAsync<T>(
        this DbContext db,
        IQueryable<T> query,
        Expression<Func<T, string?>> property,
        IPropertyValueEncryptor oldEncryptor,
        IPropertyValueEncryptor newEncryptor,
        int batchSize = 500,
        bool skipFailures = true,
        CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(oldEncryptor);
        ArgumentNullException.ThrowIfNull(newEncryptor);
        var name = property.Body is MemberExpression mm ? mm.Member.Name
            : throw new ArgumentException("Property expression must be a simple member access.", nameof(property));
        var pi = typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Property '{typeof(T).Name}.{name}' not found.");
        if (pi.GetCustomAttribute<EncryptedAttribute>() is null)
            throw new InvalidOperationException($"Re-encryption refused: '{typeof(T).Name}.{name}' lacks [Encrypted].");
        if (pi.SetMethod is null)
            throw new InvalidOperationException($"Re-encryption refused: '{typeof(T).Name}.{name}' has no setter.");
        batchSize = Math.Max(1, batchSize);

        var migrated = 0;
        var skipped = 0;
        var offset = 0;
        while (true)
        {
            var batch = await query.Skip(offset).Take(batchSize).ToListAsync(ct).ConfigureAwait(false);
            if (batch.Count == 0) break;
            if (!batch.Any(e => db.Entry(e).State != EntityState.Detached))
                throw new InvalidOperationException(
                    "ReEncryptAsync requires a tracking query (remove AsNoTracking) — otherwise nothing would be saved.");
            foreach (var entity in batch)
            {
                var current = (string?)pi.GetValue(entity);
                if (current is null) continue;
                string? plain;
                try { plain = oldEncryptor.Decrypt(current); }
                catch (Exception) when (skipFailures) { skipped++; continue; }
                if (plain is null) { skipped++; continue; }
                pi.SetValue(entity, newEncryptor.Encrypt(plain));
                migrated++;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (batch.Count < batchSize) break;
            offset += batch.Count;
        }
        return (migrated, skipped);
    }
}
