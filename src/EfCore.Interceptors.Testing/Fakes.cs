using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.Observability;

namespace EfCore.Interceptors.Testing;

/// <summary>
/// Test doubles for EfCore.Interceptors (03.17): fake user/tenant providers, recording
/// dispatchers, in-memory outbox delivery, encryptor contract checks.
/// No test-framework dependency — usable from xUnit, NUnit, MSTest or plain asserts.
/// </summary>

/// <summary>Mutable current-user stub: set <see cref="UserName"/> per test.</summary>
public sealed class FakeCurrentUserProvider(string? userName = "test-user") : ICurrentUserProvider
{
    public string? UserName { get; set; } = userName;
}

/// <summary>Mutable tenant stub: set <see cref="CurrentTenantId"/> per test.</summary>
public sealed class FakeTenantProvider(string? tenantId = "tenant-1") : ITenantProvider
{
    public string? CurrentTenantId { get; set; } = tenantId;
}

/// <summary>Accumulates dispatched domain events for assertions.</summary>
public sealed class RecordingDomainEventDispatcher : IDomainEventDispatcher
{
    public List<IDomainEvent> Dispatched { get; } = [];

    public void Dispatch(IEnumerable<IDomainEvent> domainEvents) => Dispatched.AddRange(domainEvents);

    public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        Dispatch(domainEvents);
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory <see cref="IOutboxMessageHandler"/>: delivers instantly without a processor.
/// Register in DI instead of <c>AddOutboxProcessor</c> for unit tests.
/// </summary>
public sealed class InMemoryOutboxMessageHandler(
    Func<OutboxMessage, CancellationToken, ValueTask>? handle = null) : IOutboxMessageHandler
{
    public List<OutboxMessage> Delivered { get; } = [];

    public Func<OutboxMessage, CancellationToken, ValueTask>? Handle { get; set; } = handle;

    public async ValueTask HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (Handle is not null) await Handle(message, cancellationToken).ConfigureAwait(false);
        Delivered.Add(message);
    }
}

/// <summary>
/// Framework-free contract checks for <see cref="IPropertyValueEncryptor"/>
/// implementations (07.2–07.5, 09.5 crypto-contract). Call from your test framework;
/// throws <see cref="InvalidOperationException"/> with a diagnostic message on violation.
/// </summary>
public static class EncryptorContract
{
    /// <summary>Any byte payload round-trips; AAD mismatch fails; tampered tag fails.</summary>
    public static void CheckRoundtrip(IPropertyValueEncryptor encryptor, string plaintext = "hello-мяу-🐾")
    {
        var cipher = encryptor.Encrypt(plaintext)
            ?? throw new InvalidOperationException("Encrypt returned null for non-null plaintext.");
        if (cipher == plaintext)
            throw new InvalidOperationException("Encrypt returned the plaintext unchanged.");
        var back = encryptor.Decrypt(cipher);
        if (back != plaintext)
            throw new InvalidOperationException($"Roundtrip mismatch: '{back}' != '{plaintext}'.");
        if (encryptor.Encrypt(null) is not null || encryptor.Decrypt(null) is not null)
            throw new InvalidOperationException("Encrypt/Decrypt must map null to null.");
        if (!encryptor.IsEncrypted(cipher))
            throw new InvalidOperationException("IsEncrypted must recognize own ciphertext.");
        if (encryptor.IsEncrypted(plaintext))
            throw new InvalidOperationException("IsEncrypted must not match plaintext.");
    }

    /// <summary>Flipping a ciphertext char must fail authentication (not silent garbage).</summary>
    public static void CheckTamperDetected(IPropertyValueEncryptor encryptor, string plaintext = "tamper-me")
    {
        var cipher = encryptor.Encrypt(plaintext)!;
        var chars = cipher.ToCharArray();
        chars[chars.Length / 2] = chars[chars.Length / 2] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);
        try
        {
            var result = encryptor.Decrypt(tampered);
            if (result == plaintext)
                throw new InvalidOperationException("Tampered ciphertext decrypted to the original plaintext.");
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            // Expected: authentication failure.
        }
    }
}
