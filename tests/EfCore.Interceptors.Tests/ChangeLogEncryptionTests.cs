using System.Security.Cryptography;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors.Tests;

/// <summary>
/// Security S-5: <c>[Encrypted]</c> values must never reach the <c>ChangeLog</c> in plaintext.
/// The encryption interceptor mutates <c>CurrentValue</c> to ciphertext in <c>SavingChanges</c>,
/// so the change log (running later by order contract) only ever sees ciphertext.
/// </summary>
public sealed class ChangeLogEncryptionTests
{
    private static readonly string KeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private const string CardPlaintext = "4111-1111-1111-1111";
    private const string CardUpdated = "5555-4444-3333-2222";

    private static Action<DbContextOptionsBuilder> EncryptedWithChangeLog() => o =>
        o.UseEfInterceptors(s => s
            .WithPropertyEncryption(new AesGcmPropertyValueEncryptor(KeyBase64))
            .WithChangeLog(new StaticCurrentUserProvider("auditor")));

    [Fact]
    public void Added_entity_changelog_contains_ciphertext_not_plaintext()
    {
        using var database = new SqliteTestDatabase();
        using (var db = new TestDbContext(database.BuildOptions(EncryptedWithChangeLog()).Options))
        {
            db.Database.EnsureCreated();
            db.VaultItems.Add(new VaultItem { Name = "Holder", CardNumber = CardPlaintext });
            db.SaveChanges();
        }

        using var read = new TestDbContext(database.BuildOptions().Options);
        var entry = read.ChangeLogEntries.AsNoTracking().Single(e => e.EntityName.Contains("VaultItem"));
        Assert.DoesNotContain(CardPlaintext, entry.ChangesJson);
        Assert.DoesNotContain("4111", entry.ChangesJson);
        Assert.Contains(Saving.ChangeLogSaveChangesInterceptor.RedactedMarker, entry.ChangesJson);
    }

    [Fact]
    public void Modified_entity_changelog_contains_ciphertext_not_plaintext()
    {
        using var database = new SqliteTestDatabase();
        using (var db = new TestDbContext(database.BuildOptions(EncryptedWithChangeLog()).Options))
        {
            db.Database.EnsureCreated();
            db.VaultItems.Add(new VaultItem { Name = "Holder", CardNumber = CardPlaintext });
            db.SaveChanges();
        }

        using (var db = new TestDbContext(database.BuildOptions(EncryptedWithChangeLog()).Options))
        {
            var item = db.VaultItems.Single();
            item.CardNumber = CardUpdated;
            db.SaveChanges();
        }

        using var read = new TestDbContext(database.BuildOptions().Options);
        var diffs = read.ChangeLogEntries.AsNoTracking()
            .Where(e => e.EntityName.Contains("VaultItem"))
            .Select(e => e.ChangesJson)
            .ToList();
        Assert.NotEmpty(diffs);
        foreach (var json in diffs)
        {
            Assert.DoesNotContain(CardPlaintext, json);
            Assert.DoesNotContain(CardUpdated, json);
            Assert.DoesNotContain("5555", json);
        }
    }

    [Fact]
    public void Encryption_effective_order_is_before_changelog()
    {
        var encryption = new Saving.PropertyEncryptionSaveChangesInterceptor(
            new AesGcmPropertyValueEncryptor(KeyBase64));
        var changelog = new Saving.ChangeLogSaveChangesInterceptor();
        var encryptionOrder = (encryption as IOrderedInterceptor)?.Order ?? InterceptorOrder.Audit;
        Assert.True(encryptionOrder < changelog.Order,
            $"encryption ({encryptionOrder}) must run before changelog ({changelog.Order})");
    }
}
