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

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        foreach (var property in EncryptedStringProperties(materializationData.EntityType))
        {
            var cipher = property.GetValue(entity) as string;
            if (cipher is null)
            {
                continue;
            }

            property.SetValue(entity, _encryptor.Decrypt(cipher));
        }

        return entity;
    }

    internal static IEnumerable<PropertyInfo> EncryptedStringProperties(IReadOnlyEntityType entityType)
        => entityType.GetProperties()
            .Select(p => p.PropertyInfo)
            .OfType<PropertyInfo>()
            .Where(p => p.PropertyType == typeof(string))
            .Where(p => p.GetCustomAttribute<EncryptedAttribute>() is not null);
}
