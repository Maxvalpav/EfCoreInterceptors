using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors.Testing;

/// <summary>
/// Framework-free assertions for EfCore.Interceptors (audit §4.8): audit stamping,
/// encrypted-column hygiene and cross-tenant guards. Call from xUnit/NUnit/MSTest;
/// violations throw <see cref="InvalidOperationException"/> with a diagnostic message.
/// </summary>
public static class EfInterceptorAssertions
{
    /// <summary>
    /// Saves changes and asserts every new <see cref="ChangeLogEntry"/> carries
    /// <paramref name="expectedActor"/>. Fails when no audit rows were produced
    /// (interceptor not registered or entity not audited).
    /// </summary>
    public static async Task<int> SaveChangesAndAssertAuditAsync(
        DbContext context,
        string expectedActor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expectedActor);
        var affected = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var logs = await context.Set<ChangeLogEntry>().AsNoTracking()
            .OrderByDescending(e => e.Id).Take(Math.Max(affected, 1))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (logs.Count == 0)
            throw new InvalidOperationException(
                "SaveChangesAndAssertAuditAsync: no ChangeLogEntry rows found — " +
                "is WithChangeLog registered and ChangeLogEntry mapped?");
        foreach (var log in logs)
        {
            if (!string.Equals(log.Actor, expectedActor, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Audit actor mismatch on ChangeLogEntry {log.Id}: " +
                    $"expected '{expectedActor}', got '{log.Actor}'.");
        }
        return affected;
    }

    /// <summary>
    /// Fails when any tracked <c>[Encrypted]</c> string property still holds one of the
    /// given plaintext secrets (i.e. the encryption interceptor did not run).
    /// </summary>
    public static void ShouldHaveNoPlaintextSecrets(
        DbContext context,
        params string[] plaintextSecrets)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plaintextSecrets);
        var secrets = new HashSet<string>(plaintextSecrets.Where(s => s is not null));
        foreach (var entry in context.ChangeTracker.Entries())
        {
            var type = entry.Entity.GetType();
            foreach (var prop in type.GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(string) || !prop.CanRead) continue;
                if (prop.GetCustomAttributes(typeof(EncryptedAttribute), true).Length == 0) continue;
                var value = prop.GetValue(entry.Entity) as string;
                if (value is not null && secrets.Contains(value))
                    throw new InvalidOperationException(
                        $"Plaintext secret in '{type.Name}.{prop.Name}': encryption interceptor did not encrypt the value.");
            }
        }
    }

    /// <summary>
    /// Same as <see cref="ShouldHaveNoPlaintextSecrets"/> but uses the encryptor's own
    /// <c>IsEncrypted</c> check instead of a secret list.
    /// </summary>
    public static void ShouldHaveNoPlaintextSecrets(
        DbContext context,
        IPropertyValueEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(encryptor);
        foreach (var entry in context.ChangeTracker.Entries())
        {
            var type = entry.Entity.GetType();
            foreach (var prop in type.GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(string) || !prop.CanRead) continue;
                if (prop.GetCustomAttributes(typeof(EncryptedAttribute), true).Length == 0) continue;
                var value = prop.GetValue(entry.Entity) as string;
                if (value is not null && !encryptor.IsEncrypted(value))
                    throw new InvalidOperationException(
                        $"Value in '{type.Name}.{prop.Name}' does not look like ciphertext — " +
                        "encryption interceptor did not run or the column was overwritten with plaintext.");
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="write"/> and expects <see cref="CrossTenantAccessException"/>
    /// (tenant guard works). Throws when the write succeeds — the guard is missing.
    /// </summary>
    public static void ShouldRejectCrossTenantWrite(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);
        try
        {
            write();
        }
        catch (CrossTenantAccessException)
        {
            return;
        }
        throw new InvalidOperationException(
            "ShouldRejectCrossTenantWrite: write succeeded — cross-tenant guard (WithMultiTenancy) is missing or misconfigured.");
    }

    /// <summary>Async variant of <see cref="ShouldRejectCrossTenantWrite(Action)"/>.</summary>
    public static async Task ShouldRejectCrossTenantWriteAsync(Func<Task> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (CrossTenantAccessException)
        {
            return;
        }
        throw new InvalidOperationException(
            "ShouldRejectCrossTenantWriteAsync: write succeeded — cross-tenant guard (WithMultiTenancy) is missing or misconfigured.");
    }
}
