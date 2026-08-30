using System.Security.Cryptography;
using System.Text;

namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Deterministic encryption for searchable columns (e.g. email lookup by ciphertext).
/// Uses HMAC-SHA256 derived nonce (12 bytes) + AES-GCM, so same plaintext → same ciphertext.
/// Less secure than random nonce (reveals equality), but enables WHERE EncryptedEmail = @p.
/// For non-searchable PII keep using <see cref="AesGcmPropertyValueEncryptor"/>.
/// </summary>
public sealed class DeterministicAesGcmEncryptor(string base64Key) : IPropertyValueEncryptor
{
    private readonly byte[] _key = Convert.FromBase64String(base64Key);

    public string? Encrypt(string? plaintext)
    {
        if (plaintext is null) return null;
        var nonce = DeriveNonce(plaintext);
        var tag = new byte[16];
        var cipher = new byte[Encoding.UTF8.GetByteCount(plaintext)];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(plaintext), cipher, tag);
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
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveNonce(string plaintext)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        var nonce = new byte[12];
        Buffer.BlockCopy(hash, 0, nonce, 0, 12);
        return nonce;
    }
}
