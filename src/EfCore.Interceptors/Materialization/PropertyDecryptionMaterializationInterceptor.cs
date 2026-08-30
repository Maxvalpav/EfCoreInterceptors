using System.Collections.Concurrent;
using System.Reflection;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors.Materialization;

/// <summary>
/// Decrypts [Encrypted] string properties after an entity is materialized, pairing with
/// <see cref="Saving.PropertyEncryptionSaveChangesInterceptor"/>.
/// </summary>
public class PropertyDecryptionMaterializationInterceptor(
    IPropertyValueEncryptor encryptor) : IMaterializationInterceptor
{
    private readonly IPropertyValueEncryptor _encryptor = encryptor;
    private static readonly ConcurrentDictionary<IReadOnlyEntityType, PropertyInfo[]> Cache = new();

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        var properties = Cache.GetOrAdd(materializationData.EntityType, static et =>
            et.GetProperties()
                .Select(p => p.PropertyInfo)
                .OfType<PropertyInfo>()
                .Where(p => p.PropertyType == typeof(string))
                .Where(p => p.GetCustomAttribute<EncryptedAttribute>() is not null)
                .ToArray());

        foreach (var property in properties)
        {
            var cipher = property.GetValue(entity) as string;
            if (cipher is null)
            {
                continue;
            }

            try
            {
                property.SetValue(entity, _encryptor.Decrypt(cipher));
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                // Corrupted ciphertext should not crash materialization; keep original value
                // and let caller decide. Log would be here if ILogger was injected.
            }
        }

        return entity;
    }

    internal static IEnumerable<PropertyInfo> EncryptedStringProperties(IReadOnlyEntityType entityType)
        => Cache.GetOrAdd(entityType, static et =>
            et.GetProperties()
                .Select(p => p.PropertyInfo)
                .OfType<PropertyInfo>()
                .Where(p => p.PropertyType == typeof(string))
                .Where(p => p.GetCustomAttribute<EncryptedAttribute>() is not null)
                .ToArray());
}
