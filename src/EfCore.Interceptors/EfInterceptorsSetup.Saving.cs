using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Materialization;
using EfCore.Interceptors.Saving;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors;

// Grouped SaveChanges builders (09.1: EfInterceptorsSetup split into partials).
public sealed partial class EfInterceptorsSetup
{
    /// <summary>Fills Created/Updated audit columns on IAuditableEntity entities.</summary>
    public EfInterceptorsSetup WithAuditing(
        ICurrentUserProvider? currentUserProvider = null,
        TimeProvider? clock = null)
        => Add(new AuditSaveChangesInterceptor(currentUserProvider, clock));

    /// <summary>Turns deletes of ISoftDeletableEntity entities into logical deletes.</summary>
    public EfInterceptorsSetup WithSoftDeletes(
        ICurrentUserProvider? currentUserProvider = null,
        TimeProvider? clock = null)
        => Add(new SoftDeleteSaveChangesInterceptor(currentUserProvider, clock));

    /// <summary>Publishes domain events after successful saves.</summary>
    public EfInterceptorsSetup WithDomainEvents(IDomainEventDispatcher? dispatcher = null)
        => Add(new DomainEventsSaveChangesInterceptor(dispatcher));

    /// <summary>
    /// Publishes domain events with an explicit post-commit failure policy (05.6):
    /// <c>Throw</c> (default), <c>Log</c> (swallow best-effort notifications) or
    /// <c>RouteToOutbox</c> (durable at-least-once; requires OutboxMessage mapped).
    /// </summary>
    public EfInterceptorsSetup WithDomainEvents(
        IDomainEventDispatcher? dispatcher,
        DispatchFailurePolicy failurePolicy,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
        TimeProvider? clock = null)
        => Add(new DomainEventsSaveChangesInterceptor(dispatcher, failurePolicy, loggerFactory, clock));

    /// <summary>
    /// Writes a per-property audit trail (old/new JSON diff) to the mapped ChangeLogEntries table
    /// in the same transaction. Requires modelBuilder.Entity&lt;ChangeLogEntry&gt;().
    /// </summary>
    public EfInterceptorsSetup WithChangeLog(
        ICurrentUserProvider? currentUserProvider = null,
        TimeProvider? clock = null)
        => Add(new ChangeLogSaveChangesInterceptor(currentUserProvider, clock));

    /// <summary>
    /// Atomic outbox: serializes domain events into the mapped OutboxMessages table inside the same
    /// transaction as the business change. Requires modelBuilder.Entity&lt;OutboxMessage&gt;().
    /// </summary>
    public EfInterceptorsSetup WithOutbox(TimeProvider? timeProvider = null)
        => Add(new OutboxSaveChangesInterceptor(timeProvider));

    /// <summary>Aborts SaveChanges that would touch more rows than the configured limits.</summary>
    public EfInterceptorsSetup WithMassOperationGuard(int maxAdded = 100, int maxModified = 100, int maxDeleted = 100)
        => Add(new MassOperationGuardSaveChangesInterceptor(maxAdded, maxModified, maxDeleted));

    /// <summary>Translates DbUpdateConcurrencyException into ConcurrencyConflictException.</summary>
    public EfInterceptorsSetup WithConcurrencyTranslation()
        => Add(new ConcurrencyExceptionTranslatorInterceptor());

    /// <summary>
    /// Классический «retry вокруг SaveChanges» при оптимистичной конкуренции:
    /// конфликт разрешается по политике (ClientWins/StoreWins) и сохранение повторяется
    /// до maxRetries раз с экспоненциальной задержкой. Нужен concurrency-токен в модели.
    /// </summary>
    public EfInterceptorsSetup WithConcurrencyRetry(
        ConcurrencyRetryPolicy policy = ConcurrencyRetryPolicy.ClientWins,
        int maxRetries = 3,
        TimeSpan? initialDelay = null)
        => Add(new ConcurrencyRetrySaveChangesInterceptor(policy, maxRetries, initialDelay));

    /// <summary>
    /// Точка адаптации внешних валидаторов (FluentValidation и т.п.) без зависимости от них:
    /// реализуйте IEntityValidator и передайте экземпляры сюда.
    /// </summary>
    public EfInterceptorsSetup WithCustomValidation(params IEntityValidator[] validators)
        => Add(new CustomValidationSaveChangesInterceptor(validators));

    /// <summary>
    /// Runs DataAnnotations validation over every Added/Modified entity before the save and
    /// aborts with EntityValidationException listing ALL violations at once.
    /// </summary>
    public EfInterceptorsSetup WithValidation()
        => Add(new ValidationSaveChangesInterceptor());

    /// <summary>Trims all string properties (Added/Modified) before save.</summary>
    public EfInterceptorsSetup WithStringTrimming(Func<string, string?>? normalize = null)
        => Add(new StringTrimmingSaveChangesInterceptor(normalize));

    /// <summary>Shadow-property auditing without IAuditableEntity.</summary>
    public EfInterceptorsSetup WithShadowAuditing(ICurrentUserProvider? currentUserProvider = null, TimeProvider? clock = null)
        => Add(new ShadowAuditSaveChangesInterceptor(currentUserProvider, clock));

    /// <summary>Rejects deletes of IProtectedEntity entities with ProtectedEntityException.</summary>
    public EfInterceptorsSetup WithDeleteGuard()
        => Add(new DeleteGuardSaveChangesInterceptor());

    /// <summary>Rejects modifications/deletes of IImmutableEntity (append-only) entities.</summary>
    public EfInterceptorsSetup WithImmutableGuard()
        => Add(new ImmutableEntityGuardSaveChangesInterceptor());

    /// <summary>Maintains IVersionedEntity.Version (+1 on update) for optimistic concurrency.</summary>
    public EfInterceptorsSetup WithVersionCounter()
        => Add(new VersionIncrementSaveChangesInterceptor());

    /// <summary>
    /// Transparent property encryption: [Encrypted] string properties are encrypted on save and
    /// decrypted on materialization. Provide a real IPropertyValueEncryptor in production.
    /// </summary>
    public EfInterceptorsSetup WithPropertyEncryption(IPropertyValueEncryptor encryptor)
        => Add(new PropertyEncryptionSaveChangesInterceptor(encryptor))
           .Add(new PropertyDecryptionMaterializationInterceptor(encryptor));

    /// <summary>Deterministic searchable encryption for equality lookups. Requires deterministic encryptor (e.g. DeterministicAesGcmEncryptor) — random-nonce encryptors will produce non-searchable ciphertext.</summary>
    public EfInterceptorsSetup WithSearchableEncryption(IPropertyValueEncryptor encryptor)
    {
        if (encryptor is not DeterministicAesGcmEncryptor && encryptor.GetType().Name != "DeterministicAesGcmEncryptor")
        {
            var logger = _loggerFactory?.CreateLogger("EfCore.Interceptors");
            logger?.LogWarning("WithSearchableEncryption called with non-deterministic encryptor {Type} — equality search will not work, use DeterministicAesGcmEncryptor", encryptor.GetType().Name);
        }
        return Add(new PropertyEncryptionSaveChangesInterceptor(encryptor))
            .Add(new PropertyDecryptionMaterializationInterceptor(encryptor));
    }

    /// <summary>
    /// Registers factory methods for entity types without a parameterless constructor;
    /// EF will construct those instances through the factories and bind column values on top.
    /// </summary>
    public EfInterceptorsSetup WithConstructorFactories(IReadOnlyDictionary<Type, Func<object>> factoriesByType)
    {
        AddOrReplace(new FactoryMethodInstantiationBindingInterceptor(factoriesByType));
        return this;
    }

    /// <summary>
    /// Field-level authorization (03.3): readers without the role see defaults,
    /// writers without the role get <c>FieldAuthorizationException</c>.
    /// Register after <c>WithPropertyEncryption</c> so authorization wins over decrypted values.
    /// </summary>
    public EfInterceptorsSetup WithFieldAuthorization(IRoleProvider roles)
        => Add(new FieldAuthorizationSaveChangesInterceptor(roles))
           .Add(new Materialization.FieldAuthorizationMaterializationInterceptor(roles));

    /// <summary>
    /// Write-side row-level security guard (03.2). Pair with
    /// <c>modelBuilder.ApplyRowLevelSecurity(filter)</c> in <c>OnModelCreating</c>
    /// for the query-side filter; system code escalates via <c>ElevatedSession</c>.
    /// </summary>
    public EfInterceptorsSetup WithRowLevelSecurity<T>(System.Linq.Expressions.Expression<System.Func<T, bool>> filter)
        where T : class
        => Add(new RowLevelSecuritySaveChangesInterceptor<T>(filter));

    /// <summary>
    /// Blue-green expand-contract (03.16): dual-writes <c>[MigratedFrom]</c> NEW values
    /// into OLD columns on save; pair with the fallback materializer below during the
    /// migration window, then drop the old column and the interceptors.
    /// </summary>
    public EfInterceptorsSetup WithExpandContract()
        => Add(new ExpandContractSaveChangesInterceptor())
           .Add(new Materialization.ExpandContractFallbackMaterializationInterceptor());

    /// <summary>
    /// System-versioned history for <c>[Temporal]</c> entities (03.1, SCD Type 2).
    /// Requires <c>modelBuilder.Entity&lt;TemporalRecord&gt;()</c>; read the past with
    /// <c>TemporalQuery.AsOfAsync</c>.
    /// </summary>
    public EfInterceptorsSetup WithTemporalTracking(
        ICurrentUserProvider? currentUserProvider = null,
        TimeProvider? clock = null,
        params Type[] trackedTypes)
        => Add(new TemporalSaveChangesInterceptor(currentUserProvider, clock, trackedTypes));

    /// <summary>System-versioned history for one explicit entity type (no attribute needed).</summary>
    public EfInterceptorsSetup WithTemporalTracking<T>(
        ICurrentUserProvider? currentUserProvider = null,
        TimeProvider? clock = null)
        => Add(new TemporalSaveChangesInterceptor(currentUserProvider, clock, typeof(T)));
}
