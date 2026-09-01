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
        DecryptEntity(materializationData.EntityType, entity);
        return entity;
    }

    private void DecryptEntity(IReadOnlyTypeBase type, object instance)
    {
        // Direct encrypted strings
        var properties = Cache.GetOrAdd((IReadOnlyEntityType)type, static et =>
            et.GetProperties()
                .Select(p => p.PropertyInfo)
                .OfType<PropertyInfo>()
                .Where(p => p.PropertyType == typeof(string))
                .Where(p => p.GetCustomAttribute<EncryptedAttribute>() is not null)
                .ToArray());

        foreach (var property in properties)
        {
            var cipher = property.GetValue(instance) as string;
            if (cipher is null) continue;
            try { property.SetValue(instance, _encryptor.Decrypt(cipher)); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException) { }
        }

        // Complex types (EF8+) — recursive
        foreach (var complex in type.GetComplexProperties())
        {
            var pi = complex.PropertyInfo;
            if (pi is null) continue;
            var complexInstance = pi.GetValue(instance);
            if (complexInstance is null) continue;
            DecryptComplex(complex.ComplexType, complexInstance);
        }

        // Owned navigations (fallback)
        foreach (var nav in ((IReadOnlyEntityType)type).GetNavigations().Where(n => n.TargetEntityType.IsOwned()))
        {
            var pi = nav.PropertyInfo;
            if (pi is null) continue;
            var owned = pi.GetValue(instance);
            if (owned is null) continue;
            if (owned is System.Collections.IEnumerable col && owned is not string)
            {
                foreach (var item in col) if (item is not null) DecryptEntity(nav.TargetEntityType, item);
            }
            else DecryptEntity(nav.TargetEntityType, owned);
        }
    }

    private void DecryptComplex(IReadOnlyComplexType complexType, object instance)
    {
        foreach (var prop in complexType.GetProperties())
        {
            var pi = prop.PropertyInfo;
            if (pi is null || pi.PropertyType != typeof(string)) continue;
            if (pi.GetCustomAttribute<EncryptedAttribute>() is null) continue;
            var cipher = pi.GetValue(instance) as string;
            if (cipher is null) continue;
            try { pi.SetValue(instance, _encryptor.Decrypt(cipher)); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException) { }
        }
        foreach (var nested in complexType.GetComplexProperties())
        {
            var pi = nested.PropertyInfo;
            if (pi is null) continue;
            var nestedInstance = pi.GetValue(instance);
            if (nestedInstance is null) continue;
            DecryptComplex(nested.ComplexType, nestedInstance);
        }
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
