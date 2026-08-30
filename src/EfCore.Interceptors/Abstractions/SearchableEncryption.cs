using System.Security.Cryptography;
using System.Text;

namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Deterministic encryption for searchable columns (e.g. email lookup by ciphertext).
/// Uses HMAC-SHA256 derived nonce (12 bytes) + AES-GCM, so same plaintext → same ciphertext.
/// Less secure than random nonce (reveals equality), but enables WHERE EncryptedEmail = @p.
/// For non-searchable PII keep using <see cref="AesGcmPropertyValueEncryptor"/>.
/// </summary>
public sealed class DeterministicAesGcmEncryptor : IPropertyValueEncryptor
{
    private readonly byte[] _key;

    public DeterministicAesGcmEncryptor(string base64Key)
    {
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
        {
            throw new ArgumentException("AES-256 key must be 32 bytes.", nameof(base64Key));
        }
    }

    public string? Encrypt(string? plaintext)
    {
        if (plaintext is null) return null;
        var nonce = DeriveNonce(plaintext);
        var tag = new byte[16];
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
#pragma warning disable SYSLIB0053
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
#pragma warning restore SYSLIB0053
        var payload = new byte[nonce.Length + cipher.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length, cipher.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + cipher.Length, tag.Length);
        return Convert.ToBase64String(payload);
    }

    public string? Decrypt(string? ciphertext)
    {
        if (ciphertext is null) return null;
        var payload = Convert.FromBase64String(ciphertext);
        const int nonceLen = 12, tagLen = 16;
        var cipherLen = payload.Length - nonceLen - tagLen;
        if (cipherLen < 0) throw new CryptographicException("Payload too short.");
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

    private byte[] DeriveNonce(string plaintext)
    {
        // HMAC with key instead of plain SHA256 - prevents dictionary attack without key
        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(plaintext));
        var nonce = new byte[12];
        Buffer.BlockCopy(hash, 0, nonce, 0, 12);
        return nonce;
    }
}
