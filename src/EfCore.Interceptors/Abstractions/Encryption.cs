using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using System.Text;

namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Marks a string property as encrypted at rest: the interceptor encrypts it on save and
/// decrypts on materialization. The column stores ciphertext; queries filtering on the raw
/// value will not match.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EncryptedAttribute : Attribute;

/// <summary>Encrypts/decrypts values of [Encrypted] properties.</summary>
public interface IPropertyValueEncryptor
{
    /// <summary>Returns ciphertext for storage, or null when input is null.</summary>
    string? Encrypt(string? plaintext);

    /// <summary>Returns plaintext for materialization, or null when input is null.</summary>
    string? Decrypt(string? ciphertext);
}

/// <summary>
 /// AES-GCM encryptor with version byte and optional AAD binding (table|column|pk).
 /// Payload format v1: <c>0x01 | 12-byte nonce | 16-byte tag | ciphertext</c> base64.
 /// Decrypt auto-detects legacy payload without version byte.
 /// For production prefer envelope encryption with KMS and per-record nonces.
 /// </summary>
public sealed class AesGcmPropertyValueEncryptor : IPropertyValueEncryptor, IDisposable
{
     private const byte CurrentVersion = 0x01;
     private readonly byte[] _key;
     private readonly string? _aadContext;
     private bool _disposed;

     public AesGcmPropertyValueEncryptor(string base64Key, string? aadContext = null)
     {
         _key = Convert.FromBase64String(base64Key);
         if (_key.Length != 32)
         {
             CryptographicOperations.ZeroMemory(_key);
             throw new ArgumentException("AES-256 key must be 32 bytes (Base64 of 32 bytes).", nameof(base64Key));
         }
         _aadContext = aadContext;
     }

     public void Dispose()
     {
         if (_disposed) return;
         CryptographicOperations.ZeroMemory(_key);
         _disposed = true;
     }

     /// <summary>Encrypt with optional AAD. Use same AAD on decrypt or it will fail.</summary>
     public string? Encrypt(string? plaintext, byte[]? associatedData = null)
     {
         return EncryptInternal(plaintext, associatedData ?? (_aadContext is null ? null : Encoding.UTF8.GetBytes(_aadContext)));
     }

     public string? Encrypt(string? plaintext)
     {
         return EncryptInternal(plaintext, _aadContext is null ? null : Encoding.UTF8.GetBytes(_aadContext));
     }

     private string? EncryptInternal(string? plaintext, byte[]? aad)
     {
         if (plaintext is null)
         {
             return null;
         }

         var nonce = RandomNumberGenerator.GetBytes(12);
         var tag = new byte[16];
         var plainBytes = Encoding.UTF8.GetBytes(plaintext);
         var cipher = new byte[plainBytes.Length];

#pragma warning disable SYSLIB0053
         using var aes = new AesGcm(_key, 16);
         if (aad is not null)
             aes.Encrypt(nonce, plainBytes, cipher, tag, aad);
         else
             aes.Encrypt(nonce, plainBytes, cipher, tag);
#pragma warning restore SYSLIB0053

         // payload = [version:1][nonce:12][tag:16][ciphertext]
         var payload = new byte[1 + nonce.Length + tag.Length + cipher.Length];
         payload[0] = CurrentVersion;
         Buffer.BlockCopy(nonce, 0, payload, 1, nonce.Length);
         Buffer.BlockCopy(tag, 0, payload, 1 + nonce.Length, tag.Length);
         Buffer.BlockCopy(cipher, 0, payload, 1 + nonce.Length + tag.Length, cipher.Length);

         return Convert.ToBase64String(payload);
     }

     public string? Decrypt(string? ciphertext, byte[]? associatedData = null)
     {
         return DecryptInternal(ciphertext, associatedData ?? (_aadContext is null ? null : Encoding.UTF8.GetBytes(_aadContext)));
     }

     public string? Decrypt(string? ciphertext)
     {
         return DecryptInternal(ciphertext, _aadContext is null ? null : Encoding.UTF8.GetBytes(_aadContext));
     }

     private string? DecryptInternal(string? ciphertext, byte[]? aad)
     {
         if (ciphertext is null)
         {
             return null;
         }

         var payload = Convert.FromBase64String(ciphertext);

         // Detect legacy format: no version byte (payload length does not start with 0x01, fallback)
         if (payload.Length > 0 && payload[0] == CurrentVersion && payload.Length >= 1 + 12 + 16)
         {
             const int nonceLen = 12, tagLen = 16;
             var cipherLen = payload.Length - 1 - nonceLen - tagLen;
             if (cipherLen < 0)
                 throw new CryptographicException("Encrypted payload is too short.");

             var nonce = new byte[nonceLen];
             var tag = new byte[tagLen];
             var cipher = new byte[cipherLen];
             Buffer.BlockCopy(payload, 1, nonce, 0, nonceLen);
             Buffer.BlockCopy(payload, 1 + nonceLen, tag, 0, tagLen);
             Buffer.BlockCopy(payload, 1 + nonceLen + tagLen, cipher, 0, cipherLen);

             var plain = new byte[cipherLen];
#pragma warning disable SYSLIB0053
             using var aes = new AesGcm(_key, 16);
             if (aad is not null)
                 aes.Decrypt(nonce, cipher, tag, plain, aad);
             else
                 aes.Decrypt(nonce, cipher, tag, plain);
#pragma warning restore SYSLIB0053

             return Encoding.UTF8.GetString(plain);
         }
         else
         {
             // Legacy fallback: [nonce:12][cipher][tag:16]
             const int nonceLen = 12, tagLen = 16;
             var cipherLen = payload.Length - nonceLen - tagLen;
             if (cipherLen < 0)
                 throw new CryptographicException("Encrypted payload is too short.");

             var nonce = new byte[nonceLen];
             var cipher = new byte[cipherLen];
             var tag = new byte[tagLen];
             Buffer.BlockCopy(payload, 0, nonce, 0, nonceLen);
             Buffer.BlockCopy(payload, nonceLen, cipher, 0, cipherLen);
             Buffer.BlockCopy(payload, nonceLen + cipherLen, tag, 0, tagLen);

             var plain = new byte[cipherLen];
#pragma warning disable SYSLIB0053
             using var aes = new AesGcm(_key, 16);
             aes.Decrypt(nonce, cipher, tag, plain);
#pragma warning restore SYSLIB0053

             return Encoding.UTF8.GetString(plain);
         }
     }

     /// <summary>Build deterministic AAD from table/column/pk — prevents cross-column ciphertext swap.</summary>
     public static byte[] BuildAad(string table, string column, string pk) => Encoding.UTF8.GetBytes($"{table}|{column}|{pk}");
 }
