# Changelog

## Unreleased — XL wave: temporal, dashboard, sagas (docs d/03.1, 03.13, 03.15)
- Temporal tables (03.1): `[Temporal]`, `TemporalRecord` (ticks-based SCD Type 2 —
  range predicates translate on SQLite), `TemporalSaveChangesInterceptor`
  (same-transaction close + Added key-patch mirroring ChangeLog),
  `TemporalQuery.AsOfAsync/GetHistoryAsync/Restore` (ctor-free rebuild,
  schema-drift tolerant); `WithTemporalTracking()` + `WithTemporalTracking<T>()`;
  `ChangeLog` ignores temporal rows
- Dashboard (03.13): new `EfCore.Interceptors.Dashboard` package —
  `MapEfInterceptorsDashboard<T>` (stats, outbox explorer, dead-letter retry,
  purge, changelog, cache panel) + embedded no-build HTML UI; `DashboardStore`
  is hosting-free and unit-tested, HTTP verified against real Kestrel in tests
- Sagas (03.15): `SagaInstance` + delegate-based `SagaDefinition`/`SagaStep` +
  durable `SagaRunner` (per-step transactions, reverse compensation, resume from
  `StepIndex`, terminal states); steps resolve their own DbContexts (multi-DB
  choreography via outbox); core package, no new dependencies
- Tests: 132 → 139 (`AuditP4FixesTests`: versions/AsOf/key-patch, saga
  success/compensate/resume, store + live-HTTP dashboard); provider-tripwire
  ignore extended to Postgres matrix test
- Fixes found by tests: ChangeTracker enumeration guard in temporal collect;
  (pre-existing) suite-wide `ManyServiceProvidersCreatedWarning` tripwire

## Unreleased — audit P3 wave (docs d/03: RLS, budget, expand-contract, OTel)
- Row-level security (03.2): `ApplyRowLevelSecurity(ctx, (ctx, e) => ...)` (executing-context
  substitution — nothing baked into query plans), `WithRowLevelSecurity` write guard,
  `ElevatedSession` (audited, `ef.rls.elevated`), `RowLevelSecurityException`
- Query budget (03.6): `WithQueryBudget(maxRows, scopeFilter)` +
  `QueryBudgetExceededException` via counting `BudgetDbDataReader`
- Expand-contract (03.16): `[MigratedFrom]`, dual-write interceptor, read fallback with
  `ef.expandcontract.fallbacks`, `WithExpandContract`
- New `EfCore.Interceptors.OpenTelemetry` package (03.11):
  `AddEfCoreInterceptorsInstrumentation` for both ActivitySources
- Tests: 125 → 132 (`AuditP3FixesTests`: RLS incl. no-bake proof, budgets, expand-contract,
  span flow, OTel provider wiring); `SqliteTestDatabase` ignores EF's
  many-providers tripwire (suite builds 20+ option sets by design)
- Core dependency audit (09.4): runtime closure is EF Core + Microsoft.Extensions.*
  + System.IO.Hashing only — messaging/cache/OTel stay in satellite packages

## Unreleased — audit P2/P3 enterprise wave (docs d/03, 05.6)
- Domain events: `DispatchFailurePolicy Throw/Log/RouteToOutbox` (05.6) — post-commit
  failures are logged, swallowed, or persisted as outbox rows (durable at-least-once
  without domain-code changes); `WithDomainEvents` overload + `ef.domainevents.*` metrics;
  pending is detached before dispatch (no reentrancy on nested saves)
- New `EfCore.Interceptors.HealthChecks` package (03.12): outbox lag/pending/dead-letter
  probe via `AddEfInterceptorsHealth<TContext>`
- Data classification (03.4): `[DataClassification]`, `Sensitivity`, `DataClassificationReport`
  (CLR-resilient scan), `IDataCatalogSink` export contract
- GDPR (03.5): `[SubjectIdentifier]` + `ForgetSubjectAsync(Pseudonymize/Erase)` with
  salted SHA-256, fail-closed on missing salt / non-tracking misuse
- Field-level authorization (03.3): `[RequiresRole]` + `IRoleProvider`/`StaticRoleProvider`,
  read-side defaulting + write-side `FieldAuthorizationException`, `WithFieldAuthorization`
- N+1 (03.7): opt-in `captureStackTrace` with first-user-frame call site + `.Include()` hint
- Governance (03.8): `MigrationDrift.Detect/EnsureNoDrift` + `MigrationDriftException`
- Perf (08.3): pooled per-thread JSON buffer for ChangeLog diffs; (05.8)
  `ef.savechanges.retries{policy}` counter on every concurrency retry
- Benchmarks (02.3): per-interceptor SaveChanges matrix + SELECT hit/miss/hit suite
- New `EfCore.Interceptors.Caching.Redis` package (02.8): `AddEfInterceptorsRedisCache`
  + `WithRedisCache` over shared `DistributedQueryCacheStore`
- New `EfCore.Interceptors.MassTransit` package (03.10): bus dispatcher +
  outbox-to-bus handler (verified against in-memory transport in tests)
- Tests: 111 → 125 (`AuditP2FixesTests`: policies, health, GDPR, field-auth, catalog,
  N+1 traces, drift, MassTransit roundtrip)

## Unreleased — audit P1 fixes (docs d/10.3, second wave)
- Outbox: `DispatchingOutboxMessageHandler` + `IOutboxEventHandler<T>` + `IOutboxTypeResolver`
  (typed routing instead of hand-written dispatcher, 05.5) with source-gen-friendly
  deserializer hook and `AddOutboxDispatcher` DI helper
- Outbox claim is provider-safe: lock-expiry evaluated client-side over a bounded
  candidate window (SQLite/EF cannot translate `DateTimeOffset <` or `||` in bulk ops —
  the old claim never worked on SQLite)
- ChangeLog: `ChangeLogEntryTypeConfiguration` (IX_ChangeLog_Entity/Timestamp, 02.4),
  `ChangeLogMaintenance.DeleteOlderThanAsync` retention (keyset pages, provider-safe)
- Encryption v2 (07.2): `IKeyRing` + `StaticKeyRing` + `KeyRingPropertyValueEncryptor`
  (`0x02|kid|nonce|tag|cipher`), reads v1 via kid 0, `AllowLegacyV1` gate,
  `BulkExtensions.ReEncryptAsync` rotation runner returning (migrated, skipped)
- Encryption posture (07.3): `EncryptionMigrationMode Strict/Lenient` + `DecryptionFailed`
  fallback hook on both `AesGcmPropertyValueEncryptor` and the ring encryptor
- Cache: per-table targeted invalidation (06.4) — FROM/JOIN + write-target parsing,
  `TagWith("dep:...")` contracts, `InvalidateTable`, `Generation()`; unparseable writes
  fail safe to full clear; `HybridCacheCommandInterceptor` is `[Obsolete]` (06.7)
- Setup split into partials Saving/Commands/Observability (09.1); `WithMultiTenancy`
  auto-registers `TenantModelCacheKeyFactory` (02.6); `WithIdentityResolution(bool)`
  is `[Obsolete]` (02.7); `WithSecondLevelCache` exposes size limits
- Naming (09.6): `GovernanceInterceptors.cs` split into `CommandSourceBlocker.cs` +
  `RawSqlUsageDetector.cs` with `[Obsolete]` `*Interceptor` aliases
- Metrics (08.5): SQL-tuned histogram buckets via `SharedMeter.DurationHistogram`
  (net9+ `InstrumentAdvice`; net8 uses runtime defaults)
- `MemoryQueryCacheStore` accepts `TimeProvider` (02.10 close-out, testable TTL)
- `PiiRedactor.Default` (email/phone/Luhn-checked cards/JWT, ReDoS-safe) for
  `WithSqlLogging(textRedactor:)` (02.9, 07.8); `TenantModelCacheKeyFactory` never throws
  without a registered provider
- New `EfCore.Interceptors.Testing` package (03.17): `FakeCurrentUserProvider`,
  `FakeTenantProvider`, `RecordingDomainEventDispatcher`, `InMemoryOutboxMessageHandler`,
  framework-free `EncryptorContract` checks
- Process: `CONTRIBUTING.md` (order contract table, mermaid flows, 09.7/09.8),
  CodeQL workflow, coverage artifact in CI, SBOM step + Testing pack in publish
- Tests: 91 → 111 (`AuditP1FixesTests` + order-contract/reentrancy `ArchitectureTests`)

## Unreleased — audit P0 fixes (docs d/05–07, 10.3)
- Outbox: `ClaimToken Guid?` claim instead of timestamp-equality (no cross-instance collisions); claim rewritten provider-safe (SQLite/EF10 cannot translate `DateTimeOffset <` or `||` in bulk ops — the old claim never worked on SQLite)
- Outbox: real dead-letter queue (`DeadLetteredAtUtc`, `Error`, configurable `maxAttempts`, DLQ excluded from poll) instead of comment-only; backoff with jitter
- Outbox: adaptive poll (no delay on full batch, exponential idle backoff up to 8x), `ef.outbox.*` metrics (`claimed/delivered/failed/dead_lettered/batch.duration/lag`) + `ActivitySource("EfCore.Interceptors.Outbox")` span per message
- Cache: per-key single-flight (no thundering herd), `maxRowsPerEntry`/`maxBytesPerEntry` bypass limits + `ef.cache.entry_rejected{reason}`, `ef.cache.hits/misses/serve_duration` metrics, multi-result-set never cached, `byte[]` defensive copy on serve
- Cache: `CachedDataReader` string→`DateTimeOffset`/`Guid`/enum conversion via `TypeConverter` (L2 cache previously crashed for such columns)
- Bulk: `ExecuteEncryptedUpdateAsync` / `ExecuteEncryptedUpdate` / `ExecuteEncryptedAuditedUpdateAsync` — client-side encryption for `[Encrypted]` with fail-closed guards (rejects non-`[Encrypted]` target, double-encryption)
- Encryption: `IPropertyValueEncryptor` AAD overloads fail closed (`NotSupportedException` by default) instead of silently dropping AAD; `AesGcmPropertyValueEncryptor(ReadOnlySpan<byte>)` ctor so keys avoid immutable `string` heap exposure
- Process: `SECURITY.md`, `.github/dependabot.yml`
- Tests: 80 → 91 (`AuditP0FixesTests`: outbox DLQ/claim, bulk-encrypted roundtrip + guards, cache bypass/hit/copy, encryption contracts)

## 1.0.5 — 2026-09-01
- Bulk guard `BulkOperationGuardInterceptor` + `WithBulkOperationGuard` + `BulkExtensions.ExecuteSoftDeleteAsync/ExecuteAuditedUpdateAsync` + Roslyn `EFI1001-EFI1005` (docs/bulk-operations-gap)
- Encryption v1 `0x01|nonce|tag|cipher` + AAD `BuildAad` + legacy fallback (security-audit #3)
- Tenant `TenantModelCacheKeyFactory` + `TenantId` immutable via `OriginalValue` check (security-audit #2)
- CWT + `PooledContextHelper` for `AddDbContextPool` (provider-matrix 2.1)
- `IOrderedInterceptor` + `EfInterceptorsSetup.BuildInto` deterministic order (audit -100..300)
- `ReadOnlyGuard` `GeneratedRegex(100ms)` + `CommandSource` dispatch (security-audit #7)
- `Caching` `IQueryCacheStore` + `DistributedQueryCacheStore` + `XxHash3` key + `MemoryQueryCacheStore` SizeLimit
- `NPlusOne` `XxHash3` template hash
- `ChangeLog` complex types `ComplexProperties` + owned recursion
- `PropertyEncryption` complex types + `PropertyDecryption` complex/owned
- `SqlLogging` log-injection `\r\n` sanitization
- `Metrics` low-cardinality `operation` only
- `SessionInit` SQLi doc + `BuildInto` Cosmos/InMemory warning
- `Audit` `IProperty` cache + `CreatedAtUtc` import respect
- `DomainEvents` `TransactionCommitted` defer + rollback restore
- Dual TFM `net8.0;net10.0` with `#if NET10_0_OR_GREATER` for named filters (api-design #1)
- `IdentityResolutionMode` enum to avoid boolean trap
- `PublicApiAnalyzers` + `PublicAPI.Shipped/Unshipped` + `System.IO.Hashing`
- `WithDistributedCache` / `WithSecondLevelCache(store)` overloads

## 1.0.4 — prior

Initial 60 interceptors, 76 tests.
