using System.Collections.Concurrent;
using System.Reflection;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, List<PlainBackup>> _backups = new();
    private sealed class PlainBackup
    {
        public required EntityEntry Entry;
        public required IProperty Property;
        public string? Plain;
        public ComplexPropertyEntry? ComplexEntry;
        public IProperty? ComplexProperty;
    }

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

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Restore(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Restore(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Restore(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Restore(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
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

            // Direct properties
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
                    if (_encryptor.IsEncrypted(plain)) continue;
                    GetBackupList(context).Add(new PlainBackup { Entry = entry, Property = property.Metadata, Plain = plain });
                    var aad = BuildAad(entry, property.Metadata);
                    property.CurrentValue = aad is null ? _encryptor.Encrypt(plain) : _encryptor.Encrypt(plain, aad);
                }
            }

            // Complex types (EF8+) — entry.ComplexProperties recursive (logic-audit #14, provider-matrix 2.4)
            foreach (var complex in entry.ComplexProperties)
            {
                EncryptComplex(complex, entry.State, context);
            }
        }
    }

    private void EncryptComplex(ComplexPropertyEntry complex, EntityState state, DbContext context)
    {
        foreach (var prop in complex.Properties)
        {
            if (prop.Metadata.ClrType != typeof(string) || (state == EntityState.Modified && !prop.IsModified))
                continue;
            var isEncrypted = _encryptedCache.GetOrAdd(prop.Metadata,
                p => p.PropertyInfo?.GetCustomAttribute<EncryptedAttribute>() is not null);
            if (!isEncrypted) continue;
            if (prop.CurrentValue is string plain)
            {
                if (_encryptor.IsEncrypted(plain)) continue;
                GetBackupList(context).Add(new PlainBackup { Entry = complex.EntityEntry, Property = prop.Metadata, Plain = plain, ComplexEntry = complex, ComplexProperty = prop.Metadata });
                var aad = BuildAad(complex.EntityEntry, prop.Metadata);
                prop.CurrentValue = aad is null ? _encryptor.Encrypt(plain) : _encryptor.Encrypt(plain, aad);
            }
        }
        foreach (var nested in complex.ComplexProperties)
            EncryptComplex(nested, state, context);
    }

    private List<PlainBackup> GetBackupList(DbContext context)
    {
        if (!_backups.TryGetValue(context, out var list))
        {
            list = new List<PlainBackup>();
            _backups.Add(context, list);
        }
        return list;
    }

    private void Restore(DbContext? context)
    {
        if (context is null || !_backups.TryGetValue(context, out var list)) return;
        foreach (var b in list)
        {
            try
            {
                if (b.ComplexEntry is not null && b.ComplexProperty is not null)
                {
                    var prop = b.ComplexEntry.Property(b.ComplexProperty.Name);
                    prop.CurrentValue = b.Plain;
                }
                else
                {
                    b.Entry.Property(b.Property.Name).CurrentValue = b.Plain;
                }
            }
            catch { }
        }
        _backups.Remove(context);
    }

    private byte[]? BuildAad(EntityEntry entry, IProperty property)
    {
        // AAD opt-in: only use when PK is stable (Modified) and encryptor was created with aadContext.
        // For Added with temporary PK (0), skip AAD to keep roundtrip and avoid breaking existing data.
        if (entry.State == EntityState.Added) return null;
        try
        {
            // Keep AAD disabled by default (R-26) — return null to preserve backward compatibility.
            // To enable, uncomment and ensure decryptor uses same AAD.
            return null;
            //var table = entry.Metadata.GetTableName() ?? entry.Metadata.ClrType.Name;
            //var column = property.GetColumnName() ?? property.Name;
            //var pk = entry.Metadata.FindPrimaryKey();
            //var pkVal = pk is null ? "0" : string.Join("|", pk.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString() ?? "null"));
            //return Abstractions.AesGcmPropertyValueEncryptor.BuildAad(table, column, pkVal);
        }
        catch { return null; }
    }
}
