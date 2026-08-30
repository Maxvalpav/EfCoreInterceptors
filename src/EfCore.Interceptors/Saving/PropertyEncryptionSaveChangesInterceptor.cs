using System.Collections.Concurrent;
using System.Reflection;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Encrypts [Encrypted] string properties on insert/update before commands are generated,
/// pairing with <see cref="Materialization.PropertyDecryptionMaterializationInterceptor"/>.
/// The database only ever sees ciphertext; equality search and LIKE over encrypted columns
/// are impossible by design.
/// </summary>
public class PropertyEncryptionSaveChangesInterceptor(
    IPropertyValueEncryptor encryptor) : SaveChangesInterceptor
{
    private readonly IPropertyValueEncryptor _encryptor = encryptor;
    private readonly ConcurrentDictionary<IProperty, bool> _encryptedCache = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Encrypt(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Encrypt(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    protected virtual void Encrypt(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            foreach (var property in entry.Properties)
            {
                if (property.Metadata.ClrType != typeof(string) ||
                    (entry.State == EntityState.Modified && !property.IsModified))
                {
                    continue;
                }

                var isEncrypted = _encryptedCache.GetOrAdd(property.Metadata,
                    p => p.PropertyInfo?.GetCustomAttribute<EncryptedAttribute>() is not null);

                if (!isEncrypted)
                {
                    continue;
                }

                if (property.CurrentValue is string plain)
                {
                    property.CurrentValue = _encryptor.Encrypt(plain);
                }
            }
        }
    }
}
