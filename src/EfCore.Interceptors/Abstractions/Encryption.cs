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
/// Deterministic AES-GCM encryptor keyed by a 32-byte key. Suitable as a starting point;
/// in production prefer envelope encryption with external key management (KMS/Vault)
/// and per-record nonces. GCM nonce is random per value, so equality search on ciphertexts
/// is not possible by design.
/// </summary>
public sealed class AesGcmPropertyValueEncryptor(string base64Key) : IPropertyValueEncryptor
{
    private readonly byte[] _key = Convert.FromBase64String(base64Key);

    public string? Encrypt(string? plaintext)
    {
        if (plaintext is null)
        {
            return null;
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[Encoding.UTF8.GetByteCount(plaintext)];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(plaintext), cipher, tag);

        // payload = [12-byte nonce][ciphertext][16-byte tag], stored base64
        var payload = new byte[nonce.Length + cipher.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length, cipher.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + cipher.Length, tag.Length);

        return Convert.ToBase64String(payload);
    }

    public string? Decrypt(string? ciphertext)
    {
        if (ciphertext is null)
        {
            return null;
        }

        var payload = Convert.FromBase64String(ciphertext);
        const int nonceLen = 12, tagLen = 16;
        var cipherLen = payload.Length - nonceLen - tagLen;
        if (cipherLen < 0)
        {
            throw new CryptographicException("Encrypted payload is too short.");
        }

        var nonce = new byte[nonceLen];
        var cipher = new byte[cipherLen];
        var tag = new byte[tagLen];
        Buffer.BlockCopy(payload, 0, nonce, 0, nonceLen);
        Buffer.BlockCopy(payload, nonceLen, cipher, 0, cipherLen);
        Buffer.BlockCopy(payload, nonceLen + cipherLen, tag, 0, tagLen);

        var plain = new byte[cipherLen];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }
}
