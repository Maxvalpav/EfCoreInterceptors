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

    /// <summary>
    /// Optional AAD-aware overload. Fail-closed (07.5): the default throws
    /// <see cref="NotSupportedException"/> instead of silently dropping the AAD binding,
    /// which would disable cross-column-swap protection. Custom encryptors that bind
    /// associated data MUST override this overload.
    /// </summary>
    string? Encrypt(string? plaintext, byte[]? associatedData) => throw new NotSupportedException(
        $"{GetType().Name} does not implement the AAD-aware Encrypt overload; cross-column-swap protection would be silently disabled.");
    string? Decrypt(string? ciphertext, byte[]? associatedData) => throw new NotSupportedException(
        $"{GetType().Name} does not implement the AAD-aware Decrypt overload; cross-column-swap protection would be silently disabled.");

    /// <summary>Returns true if the value looks like ciphertext produced by this encryptor.</summary>
    bool IsEncrypted(string? value) => false;
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

     /// <summary>
     /// Migration posture (07.3). <see cref="EncryptionMigrationMode.Lenient"/> (default)
     /// reads legacy unversioned payloads; <see cref="EncryptionMigrationMode.Strict"/>
     /// rejects them — use for new deployments.
     /// </summary>
     public EncryptionMigrationMode MigrationMode { get; set; } = EncryptionMigrationMode.Lenient;

     /// <summary>
     /// Lenient-mode hook (07.3): invoked with (ciphertext, aad) when authentication or
     /// decoding fails instead of throwing — return a fallback (or rethrow). Default: throw.
     /// </summary>
     public Func<string?, byte[]?, string?>? DecryptionFailed { get; set; }

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

     /// <summary>
     /// Preferred constructor (07.4): key bytes never live in an immutable
     /// <see cref="string"/> on the managed heap (heap-dump/crash-dump exposure).
     /// Pair with an <c>IKeyValueProvider</c> (env var, Azure KeyVault, AWS KMS, DPAPI).
     /// The input span is copied; the caller should zero its own buffer afterwards.
     /// </summary>
     public AesGcmPropertyValueEncryptor(ReadOnlySpan<byte> key, string? aadContext = null)
     {
         if (key.Length != 32)
             throw new ArgumentException("AES-256 key must be 32 bytes.", nameof(key));
         _key = key.ToArray();
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
          if (_disposed) throw new ObjectDisposedException(nameof(AesGcmPropertyValueEncryptor));
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
          if (_disposed) throw new ObjectDisposedException(nameof(AesGcmPropertyValueEncryptor));
          if (ciphertext is null)
          {
              return null;
          }

          try
          {
              return DecryptCore(ciphertext, aad);
          }
          catch (Exception ex) when ((ex is CryptographicException or FormatException)
              && MigrationMode == EncryptionMigrationMode.Lenient && DecryptionFailed is not null)
          {
              return DecryptionFailed(ciphertext, aad);
          }
      }

      private string? DecryptCore(string ciphertext, byte[]? aad)
      {
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
              if (MigrationMode == EncryptionMigrationMode.Strict)
                  throw new CryptographicException(
                      "Legacy (unversioned) payload rejected in Strict mode. " +
                      "Re-encrypt the row in Lenient mode first (see BulkExtensions.ReEncryptAsync).");
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

      public bool IsEncrypted(string? value)
      {
          if (string.IsNullOrEmpty(value)) return false;
          try
          {
              var payload = Convert.FromBase64String(value);
              return payload.Length >= 1 + 12 + 16 && payload[0] == CurrentVersion;
          }
          catch { return false; }
      }

    /// <summary>Build deterministic AAD from table/column/pk — prevents cross-column ciphertext swap.</summary>
    public static byte[] BuildAad(string table, string column, string pk) => Encoding.UTF8.GetBytes($"{table}|{column}|{pk}");
}

/// <summary>
/// Migration strictness for encrypted payloads (07.3).
/// <list type="bullet">
/// <item><see cref="Lenient"/> — reads v1 and legacy payloads (default, backward compatible).</item>
/// <item><see cref="Strict"/> — only versioned payloads (v1/v2); legacy heuristic fallback is rejected.</item>
/// </list>
/// </summary>
public enum EncryptionMigrationMode
{
    Lenient,
    Strict
}

/// <summary>
/// Key ring for rotation (07.2): the payload carries a key id (<c>kid</c>), so keys
/// rotate without dump-and-re-encrypt downtime. Register the retired key under its
/// old kid to keep reading old rows; new rows always use <see cref="CurrentKid"/>.
/// </summary>
public interface IKeyRing
{
    /// <summary>Kid used for newly encrypted values.</summary>
    byte CurrentKid { get; }

    /// <summary>Returns the 32-byte key for the given kid; throws when unknown.</summary>
    byte[] GetKey(byte kid);
}

/// <summary>
/// Static ring from Base64 keys: kid == index in the list. Keep the retired keys
/// appended (never reorder — kids are persisted in the payload).
/// </summary>
public sealed class StaticKeyRing : IKeyRing, IDisposable
{
    private readonly List<byte[]> _keys;
    private readonly byte _currentKid;
    private bool _disposed;

    public StaticKeyRing(IReadOnlyList<string> base64Keys, byte currentKid = 0)
    {
        if (base64Keys.Count == 0 || base64Keys.Count > 256)
            throw new ArgumentException("Provide 1..256 Base64 keys.", nameof(base64Keys));
        if (currentKid >= base64Keys.Count)
            throw new ArgumentOutOfRangeException(nameof(currentKid));
        _keys = base64Keys.Select(k =>
        {
            var bytes = Convert.FromBase64String(k);
            if (bytes.Length != 32) { CryptographicOperations.ZeroMemory(bytes); throw new ArgumentException("Each key must decode to 32 bytes (AES-256)."); }
            return bytes;
        }).ToList();
        _currentKid = currentKid;
    }

    public byte CurrentKid => _currentKid;

    public byte[] GetKey(byte kid)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StaticKeyRing));
        if (kid >= _keys.Count)
            throw new ArgumentOutOfRangeException(nameof(kid), $"Unknown key id {kid} (ring holds {_keys.Count} keys).");
        return _keys[kid];
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var k in _keys) CryptographicOperations.ZeroMemory(k);
        _disposed = true;
    }
}

/// <summary>
/// AES-GCM encryptor with key rotation (07.2).
/// Payload v2: <c>0x02 | kid:1 | 12-byte nonce | 16-byte tag | ciphertext</c> (base64).
/// Reads v1 payloads through kid 0 (gate with <see cref="AllowLegacyV1"/>); legacy
/// heuristic fallback follows <see cref="AesGcmPropertyValueEncryptor"/> rules.
/// Drop-in replacement for <see cref="AesGcmPropertyValueEncryptor"/>: pass it to
/// <c>WithPropertyEncryption</c> and to <c>ExecuteEncryptedUpdateAsync</c>.
/// </summary>
public sealed class KeyRingPropertyValueEncryptor : IPropertyValueEncryptor, IDisposable
{
    private const byte Version2 = 0x02;
    private readonly IKeyRing _ring;
    private readonly string? _aadContext;
    private readonly bool _ownsRing;
    private bool _disposed;

    /// <summary>When false, v1 payloads are rejected (Strict posture for new deployments).</summary>
    public bool AllowLegacyV1 { get; set; } = true;

    /// <summary>
    /// Lenient-mode hook (07.3): invoked with (ciphertext, aad) when authentication fails
    /// instead of throwing — return a fallback plaintext (or rethrow). Default: throw.
    /// </summary>
    public Func<string?, byte[]?, string?>? DecryptionFailed { get; set; }

    public KeyRingPropertyValueEncryptor(IKeyRing keyRing, string? aadContext = null, bool ownsRing = false)
    {
        _ring = keyRing;
        _aadContext = aadContext;
        _ownsRing = ownsRing;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsRing && _ring is IDisposable d) d.Dispose();
        _disposed = true;
    }

    private byte[]? Aad(byte[]? associatedData)
        => associatedData ?? (_aadContext is null ? null : Encoding.UTF8.GetBytes(_aadContext));

    public string? Encrypt(string? plaintext) => Encrypt(plaintext, null);

    public string? Encrypt(string? plaintext, byte[]? associatedData)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(KeyRingPropertyValueEncryptor));
        if (plaintext is null) return null;
        var aad = Aad(associatedData);
        var key = _ring.GetKey(_ring.CurrentKid);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
#pragma warning disable SYSLIB0053
        using var aes = new AesGcm(key, 16);
        if (aad is not null) aes.Encrypt(nonce, plainBytes, cipher, tag, aad);
        else aes.Encrypt(nonce, plainBytes, cipher, tag);
#pragma warning restore SYSLIB0053
        var payload = new byte[2 + nonce.Length + tag.Length + cipher.Length];
        payload[0] = Version2;
        payload[1] = _ring.CurrentKid;
        Buffer.BlockCopy(nonce, 0, payload, 2, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, 2 + nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, payload, 2 + nonce.Length + tag.Length, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    public string? Decrypt(string? ciphertext) => Decrypt(ciphertext, null);

    public string? Decrypt(string? ciphertext, byte[]? associatedData)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(KeyRingPropertyValueEncryptor));
        if (ciphertext is null) return null;
        var aad = Aad(associatedData);
        try
        {
            var payload = Convert.FromBase64String(ciphertext);
            if (payload.Length > 0 && payload[0] == Version2)
                return DecryptV2(payload, aad);
            if (payload.Length > 0 && payload[0] == 0x01 && payload.Length >= 1 + 12 + 16)
            {
                if (!AllowLegacyV1)
                    throw new CryptographicException("v1 payload rejected (AllowLegacyV1=false).");
                return DecryptV1(payload, _ring.GetKey(0), aad);
            }
            if (!AllowLegacyV1)
                throw new CryptographicException("Legacy (unversioned) payload rejected (AllowLegacyV1=false).");
            return DecryptLegacy(payload);
        }
        catch (CryptographicException) when (DecryptionFailed is not null)
        {
            return DecryptionFailed(ciphertext, aad);
        }
    }

    private string? DecryptV2(byte[] payload, byte[]? aad)
    {
        const int nonceLen = 12, tagLen = 16;
        var cipherLen = payload.Length - 2 - nonceLen - tagLen;
        if (cipherLen < 0) throw new CryptographicException("Encrypted payload is too short.");
        var kid = payload[1];
        var key = _ring.GetKey(kid);
        var nonce = new byte[nonceLen];
        var tag = new byte[tagLen];
        var cipher = new byte[cipherLen];
        Buffer.BlockCopy(payload, 2, nonce, 0, nonceLen);
        Buffer.BlockCopy(payload, 2 + nonceLen, tag, 0, tagLen);
        Buffer.BlockCopy(payload, 2 + nonceLen + tagLen, cipher, 0, cipherLen);
        var plain = new byte[cipherLen];
#pragma warning disable SYSLIB0053
        using var aes = new AesGcm(key, 16);
        if (aad is not null) aes.Decrypt(nonce, cipher, tag, plain, aad);
        else aes.Decrypt(nonce, cipher, tag, plain);
#pragma warning restore SYSLIB0053
        return Encoding.UTF8.GetString(plain);
    }

    private static string? DecryptV1(byte[] payload, byte[] key, byte[]? aad)
    {
        const int nonceLen = 12, tagLen = 16;
        var cipherLen = payload.Length - 1 - nonceLen - tagLen;
        if (cipherLen < 0) throw new CryptographicException("Encrypted payload is too short.");
        var nonce = new byte[nonceLen];
        var tag = new byte[tagLen];
        var cipher = new byte[cipherLen];
        Buffer.BlockCopy(payload, 1, nonce, 0, nonceLen);
        Buffer.BlockCopy(payload, 1 + nonceLen, tag, 0, tagLen);
        Buffer.BlockCopy(payload, 1 + nonceLen + tagLen, cipher, 0, cipherLen);
        var plain = new byte[cipherLen];
#pragma warning disable SYSLIB0053
        using var aes = new AesGcm(key, 16);
        if (aad is not null) aes.Decrypt(nonce, cipher, tag, plain, aad);
        else aes.Decrypt(nonce, cipher, tag, plain);
#pragma warning restore SYSLIB0053
        return Encoding.UTF8.GetString(plain);
    }

    private static string? DecryptLegacy(byte[] payload)
    {
        const int nonceLen = 12, tagLen = 16;
        // Legacy fallback needs the v1 key; without AAD binding there is no way to
        // know which key — reuse kid 0 is wrong in general, so fail closed here and
        // let callers migrate legacy rows with AesGcmPropertyValueEncryptor first.
        throw new CryptographicException(
            "Legacy (unversioned) payload cannot be decrypted by the key ring. " +
            "Decrypt it with the original AesGcmPropertyValueEncryptor, then re-encrypt (see BulkExtensions.ReEncryptAsync).");
    }

    public bool IsEncrypted(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try
        {
            var payload = Convert.FromBase64String(value);
            return payload.Length >= 2 + 12 + 16 && (payload[0] == Version2 || payload[0] == 0x01);
        }
        catch { return false; }
    }
}
