# EfCore.Interceptors

> Полная документация на русском: [README.ru.md](README.ru.md).

[![NuGet](https://img.shields.io/nuget/v/EfCore.Interceptors?label=NuGet&logo=nuget&color=004880)](https://www.nuget.org/packages/EfCore.Interceptors)
[![NuGet Downloads](https://img.shields.io/nuget/dt/EfCore.Interceptors?label=downloads)](https://www.nuget.org/packages/EfCore.Interceptors)
[![License: MIT](https://img.shields.io/github/license/Maxvalpav/EfCoreInterceptors?color=green)](LICENSE)
[![CI](https://github.com/Maxvalpav/EfCoreInterceptors/actions/workflows/ci.yml/badge.svg)](https://github.com/Maxvalpav/EfCoreInterceptors/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/Maxvalpav/EfCoreInterceptors/branch/master/graph/badge.svg)](https://codecov.io/gh/Maxvalpav/EfCoreInterceptors)

Production-ready interceptor suite for **Entity Framework Core** (targets `net8.0` and `net10.0`).
Covers all 7 EF Core interceptor types with everyday scenarios: auditing, soft delete, domain events & outbox,
SQL logging, slow-query detection, second-level cache, N+1 detection, query hints, read-only guard,
session init, connection/transaction lifecycle, query tree, materialization stamping and identity resolution.

```bash
dotnet add package EfCore.Interceptors
```

```csharp
using EfCore.Interceptors;

optionsBuilder.UseSqlServer(connectionString).UseEfInterceptors(s => s
    .WithAuditing(users)
    .WithSoftDeletes(users)
    .WithDomainEvents(dispatcher)
    .WithSlowQueryWarning(TimeSpan.FromSeconds(2))
    .WithSqlLogging());
```

---

## Quick start

```csharp
using EfCore.Interceptors;

optionsBuilder
    .UseSqlServer(connectionString)
    .UseEfInterceptors(s => s
        .WithAuditing(users)                                  // Created*/Updated* columns
        .WithSoftDeletes(users)                               // DELETE -> UPDATE IsDeleted=1
        .WithDomainEvents(dispatcher)                         // publish events after commit
        .WithSlowQueryWarning(TimeSpan.FromSeconds(2))        // WARN on slow queries
        .WithSqlLogging()                                     // log every SQL command
    );
```

Registration via DI:

```csharp
services.AddEfInterceptors(s => s.WithAuditing().WithSoftDeletes());
services.AddDbContext<AppDbContext>((sp, options) =>
    options.UseSqlServer(cs).UseEfInterceptorsFrom(sp));
```

> If an interceptor needs **scoped dependencies** (e.g. the current user from `IHttpContextAccessor`),
> register the interceptor itself as Scoped and resolve it manually:

```csharp
services.AddScoped<AuditSaveChangesInterceptor>();
services.AddDbContext<AppDbContext>((sp, options) =>
    options.UseSqlServer(cs).AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));
```

---

## Library contents

### 1. ISaveChangesInterceptor

| Interceptor | What it does |
|---|---|
| `AuditSaveChangesInterceptor` | Fills `CreatedAtUtc/CreatedBy/UpdatedAtUtc/UpdatedBy` on `IAuditableEntity`. Protects Created* from tampering on update (`IsModified = false`). Supports `TimeProvider` for tests. |
| `SoftDeleteSaveChangesInterceptor` | Turns `Remove()` into logical delete on `ISoftDeletableEntity`: `State = Modified`, sets `IsDeleted/DeletedAtUtc/DeletedBy`. The row stays in the DB. Add a global filter `HasQueryFilter(e => !e.IsDeleted)` to hide it from queries — the interceptor only owns the write side. |
| `DomainEventsSaveChangesInterceptor` | Outbox-style dispatch of domain events: snapshots `IHasDomainEvents` aggregates before save, publishes them via `IDomainEventDispatcher` after a successful commit, then clears. On save failure the events stay on the aggregates for retry. |
| `ChangeLogSaveChangesInterceptor` | **Audit trail in the DB**: writes entity name, serialized PK, action (Added/Modified/Deleted), JSON diff of changed properties (old/new) and the actor into `ChangeLogEntry` — in the same transaction as the business change. Requires mapping `ChangeLogEntry`. `[Encrypted]` values are stored as `[encrypted]` markers, never plaintext; `[ExcludeFromChangeLog]` skips a property entirely. |
| `OutboxSaveChangesInterceptor` | **Transactional outbox**: serializes aggregate domain events into `OutboxMessage` rows inserted in the same transaction; a background worker delivers them and stamps `ProcessedAtUtc`. On save failure the events return to the aggregates. Requires mapping `OutboxMessage`. |
| `MultiTenancySaveChangesInterceptor` | **Multi-tenancy**: stamps `TenantId` on `ITenantEntity` inserts from `ITenantProvider`; cross-tenant modification raises `CrossTenantAccessException`. Add a tenant query filter for the read side. `WithMultiTenancy()` also forbids `IgnoreQueryFilters()` via an auto-installed strict policy (opt out with `strictQueryPolicy: false`); raw SQL can be locked behind `TagWith("tenant-safe")` with `WithStrictQueryPolicy(forbidUntaggedRawSql: true)`. Threat model: [THREAT-MODEL.md](THREAT-MODEL.md). |
| `MassOperationGuardSaveChangesInterceptor` | Guard against mass operations: if one SaveChanges touches more than N Added/Modified/Deleted entities, throws `MassOperationException` with statistics. |
| `ConcurrencyExceptionTranslatorInterceptor` | Translates low-level `DbUpdateConcurrencyException` into domain `ConcurrencyConflictException` so upper layers don't depend on EF. |
| `DeleteGuardSaveChangesInterceptor` | Absolute delete ban for `IProtectedEntity` (financial/legal records) — `ProtectedEntityException` before hitting the DB. |
| `ImmutableEntityGuardSaveChangesInterceptor` | Append-only `IImmutableEntity`: inserts allowed, update/delete raise `ImmutableEntityException`. |
| `PropertyEncryptionSaveChangesInterceptor` + `PropertyDecryptionMaterializationInterceptor` | Transparent `[Encrypted]` property encryption: AES-GCM ciphertext in the DB, decrypted on materialization. Implement `IPropertyValueEncryptor` (ready-made `AesGcmPropertyValueEncryptor`, key-ring `KeyRingPropertyValueEncryptor` with payload v2 `0x02|kid|nonce|tag|cipher`). |
| `ValidationSaveChangesInterceptor` | Aggregated DataAnnotations validation before save: every violation of every entity in one `EntityValidationException`, instead of failing on the first DB error. |
| `VersionIncrementSaveChangesInterceptor` | Maintains `IVersionedEntity.Version` (+1 per update) for optimistic concurrency on providers without rowversion; declare the property as a concurrency token and stale writes get rejected. |
| `ConcurrencyRetrySaveChangesInterceptor` | Classic "retry around SaveChanges" as an interceptor: on token conflict applies **ClientWins** (last-write-wins) or **StoreWins** (reload) policy, retries up to N times with exponential backoff; the caller sees success. After exhaustion the original `DbUpdateConcurrencyException` propagates. |
| `CustomValidationSaveChangesInterceptor` | Adapter for external validators without taking a dependency: implement `IEntityValidator` (FluentValidation etc.) — aggregated errors in one `EntityValidationException`. |

### 2. IDbCommandInterceptor

| Interceptor | What it does |
|---|---|
| `SqlLoggingCommandInterceptor` | Logs every SQL command: start (Debug), completion with duration (Information), cancellation (Information), error (Error). `includeParameterValues` shows/hides parameter values; `textRedactor` masks PII/cards in everything that reaches the log. |
| `SlowQueryCommandInterceptor` | Warning when a command exceeds the threshold. Uses `CommandExecutedEventData.Duration` — no hand-rolled stopwatches. Optional predicate selects which commands to check. |
| `QueryHintsCommandInterceptor` | Appends provider-specific hints to SQL. Hint selection via `TagWith("key")` tags (dictionary `tag -> hint`) or an arbitrary predicate over the SQL text. SQL Server example: `{"recompile": "OPTION (RECOMPILE)"}`. |
| `ReadOnlyGuardCommandInterceptor` | Blocks writes: any INSERT/UPDATE/DELETE/DDL throws `ReadOnlyContextException` before touching the DB. Enabled by predicate (e.g. reporting context only). Reads unaffected. |
| `CachingCommandInterceptor` | **Second-level cache**: identical SELECTs (SQL + parameter values) are served from memory without hitting the DB; TTL, manual invalidation (`InvalidateAll` / `Invalidate("TableName")`), `invalidateOnWrites: true` clears the cache after any committed write, disabled by default inside explicit transactions. Only SELECT/WITH are cached. Distributed stores available (`IQueryCacheStore`, Redis package). |
| `NPlusOneDetectorCommandInterceptor` | N+1 detector: EF parameterizes queries, so repeating the same SQL template within one context is the classic N+1 sign. One Warning per template after the threshold. |
| `MetricsCommandInterceptor` | System.Diagnostics.Metrics: `ef.command.duration` histogram (ms) plus `ef.command.executed` / `ef.command.failed` counters — OTel/MeterListener compatible. |
| `WritesRequireTransactionCommandInterceptor` | Unit-of-work discipline: a write command outside an explicit transaction (e.g. ad-hoc `ExecuteSqlRaw`) throws `MissingTransactionException`. |
| `CommandTimeoutCommandInterceptor` | Dynamic `CommandTimeout` per command: selector over SQL text or tag dictionary (`TagWith("report")` → 300 s), everything else uses the context default. |
| `CommandSourceBlocker` | Governance: blocks commands by source — by default forbids running EF migrations from app runtime (`BlockedCommandSourceException`); can also block ExecuteSqlRaw/ExecuteDelete etc. |
| `RawSqlUsageDetector` | Transparency for bypasses: Warning + callback on every FromSqlRaw/SqlQuery/ExecuteSqlRaw. |

### 3. IDbConnectionInterceptor

| Interceptor | What it does |
|---|---|
| `ConnectionLifecycleLoggingInterceptor` | Open/close/error logging with duration; masks `Password/Pwd/Secret/Token` in the connection string. |
| `SessionInitConnectionInterceptor` | Runs a list of SQL statements on every opened connection — settings not expressible via connection string: `SET TRANSACTION ISOLATION LEVEL ...` / `EXEC sp_set_session_context 'TenantId', @p` (SQL Server), `SET search_path TO ...` (PostgreSQL), `PRAGMA foreign_keys=ON` (SQLite). |
| `DynamicConnectionStringConnectionInterceptor` | **Connection routing**: resolves the connection string at open time via callback — database-per-tenant, read/write split to a replica, dynamic failover targets. |

### 4. IDbTransactionInterceptor

| Interceptor | What it does |
|---|---|
| `TransactionLifecycleLoggingInterceptor` | Full transaction lifecycle: begin, commit, rollback (Warning), savepoints, errors (Error). |
| `ForcedIsolationLevelTransactionInterceptor` | Forces every transaction (including implicit SaveChanges ones) to start with the given `IsolationLevel` by intercepting transaction creation. Mind provider capabilities: SQL Server honors levels, SQLite ignores them. |

### 5. IQueryExpressionInterceptor

| Interceptor | What it does |
|---|---|
| `QueryTreeLoggingInterceptor` | Logs the LINQ expression tree of every query before compilation (Debug). Extension point: inherit and override `Transform(Expression)` — the returned tree gets compiled instead. |
| `StrictQueryPolicyQueryExpressionInterceptor` | Compliance guard: forbids dangerous query shapes — `IgnoreQueryFilters()` (by default), optionally `ExecuteDelete()`/`ExecuteUpdate()` — throwing `QueryPolicyViolationException` at query-compile time. |

### 6. IMaterializationInterceptor

| Interceptor | What it does |
|---|---|
| `LoadStampingMaterializationInterceptor` | Stamps `LoadedAtUtc` on `ILoadTimestamped` entities at materialization — shows how stale an in-memory object is. |
| `InitializationMaterializationInterceptor` | Calls `IInitializable.OnLoaded()` after materialization — a place to recompute transient state. |
| `FactoryMethodInstantiationBindingInterceptor` | Covers `IInstantiationBindingInterceptor`: swaps constructor binding for a `Type -> Func<object>` factory dictionary, for legacy types without a usable constructor. |
| `RequireQueryTagsInterceptor` | Requires `TagWith(...)` tags: an exact set or "at least one" — otherwise `QueryPolicyViolationException`. Makes "which feature issued this query" answerable from the SQL comment in traces. |
| `PropertyDecryptionMaterializationInterceptor` | Decrypts `[Encrypted]` properties (pair of `PropertyEncryptionSaveChangesInterceptor`). |

### 7. IIdentityResolutionInterceptor

| Interceptor | What it does |
|---|---|
| `OverwriteIdentityResolutionInterceptor` / `IgnoreIncomingIdentityResolutionInterceptor` | Resolve key conflicts when `Attach`/`Add`-ing a second copy of the same entity: incoming values overwrite the tracked ones (last-write-wins) or are ignored (cache semantics). EF Core 10 also ships built-in `UpdatingIdentityResolutionInterceptor` / `IgnoringIdentityResolutionInterceptor`. |
| `NullMergeIdentityResolutionInterceptor` | Null-preserving merge: incoming values only fill null/empty properties of the tracked instance — non-empty data always wins. |
| `NewestWinsIdentityResolutionInterceptor` | Last-write-wins by timestamps: the instance with the fresher `UpdatedAtUtc` survives. |

### 8. Model helpers (not interceptors, but complete the picture)

```csharp
modelBuilder.ApplySoftDeleteFilters();            // !IsDeleted for all ISoftDeletableEntity
modelBuilder.ApplyTenantFilters(tenantProvider);  // TenantId == current for all ITenantEntity
```

Filters merge with existing ones via AndAlso (anonymous) or are added as separate
named EF 10 filters (`EfCoreInterceptors.SoftDelete` / `.Tenant`) — nothing is overwritten.
`ApplyRowLevelSecurity(ctx, (ctx, e) => ...)` adds executing-context RLS predicates.

### 9. Observability (System.Diagnostics.Metrics)

All metrics are published to the `EfCore.Interceptors` meter — attach a MeterListener/OTLP exporter.

| Interceptor | Instruments |
|---|---|
| `SaveChangesMetricsInterceptor` | `ef.save.duration` (histogram, ms), `ef.save.executed`, `ef.save.failed`, `ef.save.entities` |
| `TransactionMetricsInterceptor` | `ef.transaction.started/committed/rolledback/failed`, `ef.transaction.duration` |
| `ConnectionMetricsInterceptor` | `ef.connection.opened/closed/failed`, `ef.connection.open_duration` |
| `LongRunningTransactionDetector` | Warning log when a transaction is held longer than the threshold (locks/vacuum) |
| `SlowSaveChangesDetector` | Warning log when a single SaveChanges exceeds the threshold |
| `CommandsPerSaveDiagnosticInterceptor` | How many SQL commands one SaveChanges produced — catches hidden "N+1 on write" |
| `MaterializationMetricsInterceptor` | `ef.materialization.entities` — a spike means cartesian explosion / forgotten pagination |
| `MetricsCommandInterceptor` (see above) | `ef.command.duration/executed/failed` |

Reading metrics in tests/console:

```csharp
using var listener = new MeterListener();
listener.InstrumentPublished = (instr, l) =>
{
    if (instr.Meter.Name == "EfCore.Interceptors") l.EnableMeasurementEvents(instr);
};
listener.SetMeasurementEventCallback<long>((inst, value, tags, _) => Console.WriteLine($"{inst.Name}: {value}"));
listener.Start();
```

---

## Entity contracts

```csharp
public interface IAuditableEntity      { DateTimeOffset CreatedAtUtc { get; set; } string? CreatedBy { get; set; } DateTimeOffset? UpdatedAtUtc { get; set; } string? UpdatedBy { get; set; } }
public interface ISoftDeletableEntity  { bool IsDeleted { get; set; } DateTimeOffset? DeletedAtUtc { get; set; } string? DeletedBy { get; set; } }
public interface ILoadTimestamped      { DateTimeOffset? LoadedAtUtc { get; set; } }

public interface IDomainEvent          { DateTimeOffset OccurredAtUtc { get; } }
public interface IHasDomainEvents      { IReadOnlyList<IDomainEvent> DomainEvents { get; } void AddDomainEvent(IDomainEvent e); void ClearDomainEvents(); }
public interface IDomainEventDispatcher{ void Dispatch(IEnumerable<IDomainEvent> events); Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken ct = default); }
public interface ICurrentUserProvider  { string? UserName { get; } }   // + StaticCurrentUserProvider
public interface ITenantEntity         { string? TenantId { get; set; } }
public interface ITenantProvider       { string? CurrentTenantId { get; } }  // + StaticTenantProvider
```

Contract properties must be mapped in the model.

Domain exceptions: `CrossTenantAccessException`, `MassOperationException`, `ConcurrencyConflictException`, `QueryPolicyViolationException`, `MissingTransactionException`, `ReadOnlyContextException`.

Ready-made mappable entities (namespace `EfCore.Interceptors.Entities`): `ChangeLogEntry` (audit trail) and `OutboxMessage` (outbox) — add them to the model:
```csharp
modelBuilder.Entity<ChangeLogEntry>();
modelBuilder.Entity<OutboxMessage>();
```

## Typical wiring: soft delete end-to-end

```csharp
// 1. Entity
public class Order : ISoftDeletableEntity, IAuditableEntity { /* ... */ }

// 2. Global read-side filter
modelBuilder.Entity<Order>().HasQueryFilter(o => !o.IsDeleted);

// 3. Write-side registration
.UseEfInterceptors(s => s.WithSoftDeletes(users))
```

## Domain events end-to-end

```csharp
public class Order : IHasDomainEvents
{
    public int Id { get; set; }
    public decimal Total { get; set; }
    public bool IsPaid { get; private set; }

    private readonly List<IDomainEvent> _events = [];
    [NotMapped] public IReadOnlyList<IDomainEvent> DomainEvents => _events;
    public void AddDomainEvent(IDomainEvent e) => _events.Add(e);
    public void ClearDomainEvents() => _events.Clear();

    public void MarkPaid()
    {
        IsPaid = true;
        AddDomainEvent(new OrderPaid(Id, Total));
    }
}

public sealed record OrderPaid(int OrderId, decimal Total) : IDomainEvent
{
    public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
}
```

---

## Registration API

**On the options builder** (works everywhere, DI not required):
`UseEfInterceptors(Action<EfInterceptorsSetup>)` — fluent: `WithAuditing`, `WithSoftDeletes`,
`WithDomainEvents`, `WithSqlLogging`, `WithSlowQueryWarning`, `WithQueryHints`, `WithReadOnlyGuard`,
`WithSessionInit`, `WithConnectionLogging`, `WithTransactionLogging`, `WithQueryTreeLogging`,
`WithLoadStamping`, `WithIdentityResolution(overwriteExisting)`,
`WithChangeLog`, `WithOutbox`, `WithMultiTenancy(provider)`, `WithMassOperationGuard(...)`,
`WithConcurrencyTranslation()`, `WithSecondLevelCache(ttl)`, `WithNPlusOneDetection(threshold)`,
`WithCommandMetrics(meterName)`, `WithTransactionalWrites()`, `WithDynamicConnectionString(resolver)`,
`WithForcedIsolationLevel(level)`, `WithStrictQueryPolicy(...)`,
`WithNullMergingIdentityResolution()`,
`WithDeleteGuard()`, `WithImmutableGuard()`, `WithSaveChangesMetrics()`,
`WithTransactionMetrics()`, `WithConnectionMetrics()`,
`WithLongRunningTransactionDetection(threshold)`, `WithCommandTimeout(...)` / `WithCommandTimeoutByTags(...)`,
`WithInitialization()`, `WithPropertyEncryption(encryptor)`, `WithCommandSourceBlocker(...)`, `WithRawSqlUsageDetection(...)`, `WithVersionCounter()`, `WithNewestWinsIdentityResolution()`, `WithMaterializationMetrics()`, `WithRequiredQueryTags(...)` / `WithRequireAnyQueryTag()`,
`WithValidation()`, `WithSlowSaves(threshold)`, `WithConstructorFactories(map)`, `WithConcurrencyRetry(policy, maxRetries)`, `WithCustomValidation(...)`, `WithCommandsPerSaveDiagnostics(n)`
+ `Add(IInterceptor)` for your own.

Model side: `modelBuilder.ApplySoftDeleteFilters()` and `modelBuilder.ApplyTenantFilters(provider)`.

**Via DI**: `services.AddEfInterceptors(...)` + `.UseEfInterceptorsFrom(serviceProvider)`.

**Startup validation** (fail fast on silent misconfiguration — soft-delete without a filter,
ChangeLog/Outbox without mapped entities, duplicates, provider fit):

```csharp
Action<EfInterceptorsSetup> configure = s => s.WithAuditing().WithSoftDeletes().WithChangeLog();
optionsBuilder.UseEfInterceptors(configure);

// ...at startup, against the built context:
var setup = new EfInterceptorsSetup();
configure(setup);
setup.ValidateAndThrow(db);   // throws on the first error; setup.Validate(db) returns all findings
```


---

## Working example: ASP.NET Core (samples/WebApiSample)

Run and verify live:

```bash
dotnet run --project samples/WebApiSample --urls http://localhost:5000
# in another terminal:
curl -X POST http://localhost:5000/products -H "X-User: alice" \
     -H "Content-Type: application/json" -d '{"name":"Keyboard","price":49.9}'
curl http://localhost:5000/outbox        # the processor already delivered events (ProcessedAtUtc != null)
```

The key trick — **scoped interceptors for per-request state** (current user from a header):

```csharp
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserProvider, HttpContextCurrentUserProvider>(); // reads X-User
builder.Services.AddScoped<AuditSaveChangesInterceptor>();                          // the interceptor is scoped too

builder.Services.AddDbContext<ProductDbContext>((sp, options) => options
    .UseSqlite(cs)
    .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>())   // ← from the container
    .UseEfInterceptors(s => s
        .WithSoftDeletes()                              // stateless — inline is fine
        .WithOutbox()                                   // events -> table in the same transaction
        .WithSlowQueryWarning(TimeSpan.FromMilliseconds(300))
        .WithSqlLogging(sampleRate: 0.25)               // 25% of SQL logs, errors — always
        .WithCommandMetrics()
        .WithNPlusOneDetection(5)));

// background outbox delivery:
builder.Services.AddScoped<IOutboxMessageHandler, ProductCreatedHandler>();
builder.Services.AddOutboxProcessor<ProductDbContext>(pollInterval: TimeSpan.FromSeconds(1));
```

Demo endpoints: `GET/POST/DELETE /products`, `POST /products/{id}/restore` (recycle bin),
`GET /outbox` (queue and delivery statuses).

---

## Project ecosystem

### MediatR integration (`src/EfCore.Interceptors.MediatR`)

Bridge between domain events and the MediatR pipeline:

```csharp
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
builder.Services.AddMediatRDomainEventDispatcher();   // IDomainEventDispatcher -> IMediator

// handlers subscribe to the wrapper:
public class ProductCreatedHandler
    : INotificationHandler<DomainEventNotification<ProductCreated>>
{
    public Task Handle(DomainEventNotification<ProductCreated> n, CancellationToken ct) { ... }
}
```

### MassTransit bridge (`src/EfCore.Interceptors.MassTransit`)

Publish domain events through the MassTransit bus, including an outbox-to-bus handler:

```bash
dotnet add package EfCore.Interceptors.MassTransit
```

### Shared cache over Redis (`src/EfCore.Interceptors.Caching.Redis`)

Multi-instance second-level cache via `IDistributedCache`:

```csharp
services.AddEfInterceptorsRedisCache(...);   // DistributedQueryCacheStore
s.WithRedisCache(...);                       // route the command interceptor at Redis
```

### OpenTelemetry wiring (`src/EfCore.Interceptors.OpenTelemetry`)

```csharp
services.AddEfCoreInterceptorsInstrumentation();  // registers both ActivitySources with the tracer provider
```

### Health checks (`src/EfCore.Interceptors.HealthChecks`)

Outbox lag/pending/dead-letter probes:

```csharp
services.AddEfInterceptorsHealth<TContext>();  // AddEfInterceptorsHealth check
```

### Dashboard (`src/EfCore.Interceptors.Dashboard`)

Opt-in web UI: stats, outbox explorer, dead-letter retry/purge, changelog viewer, cache panel:

```csharp
app.MapEfInterceptorsDashboard<TContext>();  // protect with RequireAuthorization() in production
```

### Testing doubles (`src/EfCore.Interceptors.Testing`)

Framework-free fakes: `FakeCurrentUserProvider`, `FakeTenantProvider`,
`RecordingDomainEventDispatcher`, `InMemoryOutboxMessageHandler`, encryptor contract checks.

### Roslyn analyzers (EFI1001–EFI1005 bulk guards + EFI2001/EFI2003 strictness, shipped inside the core package)

Bulk-bypass guards: warn when `ExecuteDelete`/`ExecuteUpdate`/`SetProperty` touches
`ISoftDeletableEntity`, `[Encrypted]` or `TenantId` instead of the safe `BulkExtensions` helpers.
Strictness guards: `EFI2001` flags `IgnoreQueryFilters()` (bypasses soft-delete/tenant filters),
`EFI2003` flags `Where()` over `[Encrypted]` properties (ciphertext never matches plaintext).

### Benchmarks (`benchmarks/EfCore.Interceptors.Benchmarks`)

Overhead of the full interceptor set on SaveChanges vs. a bare context:

```bash
dotnet run -c Release --project benchmarks/EfCore.Interceptors.Benchmarks --filter *SaveChanges*
```

### CI

`.github/workflows/ci.yml`: format check → build/test matrix (`net8.0`/`net10.0` × ubuntu/windows,
139 tests per runtime) → coverage upload (Codecov) → public-API check → pack of every package.

### Package

```bash
dotnet pack EfCoreInterceptors.slnx -c Release -o artifacts
# artifacts/EfCore.Interceptors.<version>.nupkg (+ snupkg), README inside the package
```

## Bulk-operation limitations — critical

`ExecuteUpdate` / `ExecuteDelete` (EF Core 7+) translate directly into `UPDATE … WHERE` / `DELETE … WHERE`
and **do not go through `ISaveChangesInterceptor`**.
So they bypass: soft delete → physical delete, encryption → plaintext write, guards/validation/audit/ChangeLog/Outbox/domain events — all silently.

| Interceptor | What happens on bulk | Severity |
|---|---|---|
| `SoftDeleteSaveChangesInterceptor` | Physical delete | 🔴 data loss |
| `PropertyEncryptionSaveChangesInterceptor` | Plaintext in a ciphertext column | 🔴 leak/corruption |
| `DeleteGuard` / `ImmutableGuard` | Delete without exception | 🔴 compliance |
| `MultiTenancySaveChangesInterceptor` | `SetProperty(e=>e.TenantId,"other")` hands rows to another tenant | 🔴 security |
| `ChangeLog` / `Audit` / `Outbox` / `DomainEvents` / `Validation` | Silently skipped | 🟠 |

Protection:
```csharp
// Layer 1 — guard (shipped in the library):
.UseEfInterceptors(s => s.WithBulkOperationGuard(BulkOperationPolicy.Throw))

// Layer 2 — strict policy at LINQ compile time:
.UseEfInterceptors(s => s.WithStrictQueryPolicy(forbidExecuteDelete: true, forbidExecuteUpdate: true))

// Safe alternatives — library helpers:
await db.Orders.Where(...).ExecuteSoftDeleteAsync(users, timeProvider, ct);
await db.Orders.Where(...).ExecuteAuditedUpdateAsync(...);
await db.Secrets.Where(...).ExecuteEncryptedUpdateAsync(e => e.Value, plain, encryptor, ct);
```

## Providers and compatibility

| Provider | Notes |
|---|---|
| SQL Server, PostgreSQL, SQLite, MySQL/Pomelo | Full support for relational interceptors |
| Cosmos (non-relational) | `IDbCommandInterceptor`/`IDbConnectionInterceptor`/`IDbTransactionInterceptor` are not invoked — only SaveChanges/Materialization/IdentityResolution work |
| InMemory | Not relational — command tests on it are meaningless for `SqlLogging`/`Caching`/`N+1` |

EF Core notes:
- **`AddDbContextPool` breaks per-`DbContext` state** (domain events/outbox counters leak between requests). Implement `IResettableService.ResetState()` on the context or document the incompatibility.
- **Compiled models** freeze query filters — a runtime-dependent tenant filter won't apply.
- **Complex types** (`entry.ComplexProperties`) are honored in audit/ChangeLog/encryption (recursive walk).
- **Encryption**: payload v1 = `0x01|nonce|tag|cipher` + AAD (`table|column|pk`) against ciphertext swap across columns; key-ring payload v2 = `0x02|kid|nonce|tag|cipher` with `ReEncryptAsync` rotation runner; legacy payloads decrypt via fallback.
- **Named query filters** (`HasQueryFilter(name)`) require .NET 10 / EF 10; on `net8.0` anonymous filters are used.

## Notes and recommendations

- **Lifetimes.** Interceptors are registered per `DbContext`; the classes themselves are stateless and thread-safe (except documented domain-event/outbox buffers keyed by context instance via `ConditionalWeakTable`). One instance can be shared across contexts — that's how cache invalidation works (`WithSecondLevelCache` + shared `CachingCommandInterceptor`).
- **Multi-tenancy**: `ApplyTenantFilters(provider)` captures the provider in the model cache — the first tenant "sticks". Register `TenantModelCacheKeyFactory` (`options.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()`) or filter via a context property `e => e.TenantId == CurrentTenantId`. Changing `TenantId` after creation is forbidden (immutable) — `CrossTenantAccessException` when `OriginalValue != CurrentValue`.
- **Soft delete doesn't filter reads.** Always add a global query filter; otherwise deleted rows stay visible. `IgnoreQueryFilters()` also reveals them (handy for a recycle bin) — and `WithStrictQueryPolicy` can forbid such calls.
- **Domain events** dispatch strictly after commit (at-least-once in-process). Handler threw — you get `InvalidOperationException`; data is already saved. For guaranteed delivery use `WithOutbox()`: events land in a table in the same transaction. The outbox processor uses `LockedUntilUtc/AttemptCount` + `FOR UPDATE SKIP LOCKED`-style claim for multi-instance.
- **ChangeLog/Outbox** require mapped `ChangeLogEntry`/`OutboxMessage` entities. The diff covers all properties for Added/Deleted and changed ones for Modified (including owned). For `Added` with DB-generated PKs the key is patched with a second `SaveChanges` in the same transaction.
- **Second-level cache** is in-process with `SizeLimit` and invalidation on `TransactionCommitted`. Enable `invalidateOnWrites: true` so writes auto-clear the cache after commit, or call `Invalidate*()` after external changes. Inside explicit transactions the cache is bypassed by default. For multi-instance use `IQueryCacheStore` / Redis.
- **QueryHints** modify SQL text — test against the target provider (a SQL Server hint fails on SQLite with a syntax error; use comments as in the sample). Batches with multiple statements are not patched.
- **ReadOnlyGuard / StrictQueryPolicy** — the guard now looks at `CommandSource` (SaveChanges/Migrations/BulkUpdate) and at text via `[GeneratedRegex]` only for `ExecuteSqlRaw` (best-effort). The policy checks query shapes at compile time.
- **`[Encrypted]` columns** hold ciphertext: equality/LIKE search over encrypted columns is impossible (random nonce). `AesGcmPropertyValueEncryptor` v1: `version|nonce|tag|cipher` + optional AAD (`table|column|pk`) against ciphertext shuffling; legacy payloads decrypt via fallback. For production use envelope encryption with an external KMS. Encryption happens on the client.
- **ForcedIsolationLevel** applies to all context transactions, including implicit SaveChanges ones — evaluate the locking impact.
- **Audit** trusts `ICurrentUserProvider`. In web apps implement it over `IHttpContextAccessor` and register the interceptor as Scoped. Prefer `WithIdentityResolution(IdentityResolutionMode.Overwrite)` over the `bool` overload (avoids the boolean trap). `CreatedAtUtc` is preserved on import when already set; `UpdatedAtUtc` is `DateTimeOffset?`.
- Interceptors run synchronously on the request hot path — avoid heavy work.

## Solution structure

```
src/EfCore.Interceptors/         # library (net8.0;net10.0)
  Abstractions/                  # entity contracts, markers, encryption
  Entities/                      # ChangeLogEntry, OutboxMessage
  Saving/ Commands/ Connections/ # interceptors by type
  Transactions/ Queries/
  Materialization/ Tracking/
  Observability/                 # metrics and watchdogs
  Sagas/ TemporalQuery.cs ...    # XL wave: sagas, temporal, RLS, GDPR, expand-contract
src/EfCore.Interceptors.{MediatR,MassTransit,Caching.Redis,OpenTelemetry,HealthChecks,Dashboard,Testing}/
src/EfCore.Interceptors.Analyzers/   # Roslyn EFI1001-EFI1005 (shipped inside the core package)
samples/SampleApp/               # demo of all scenarios (SQLite): dotnet run --project samples/SampleApp
samples/WebApiSample/            # ASP.NET Core demo with outbox: see above
tests/EfCore.Interceptors.Tests/ # xUnit: 139 tests × (net8.0, net10.0)
benchmarks/EfCore.Interceptors.Benchmarks/
```

## Verification

```bash
dotnet build EfCoreInterceptors.slnx -c Release
dotnet test tests/EfCore.Interceptors.Tests -c Release   # both TFMs
dotnet run --project samples/SampleApp                   # console run of all scenarios
```

---

- Full Russian docs: [README.ru.md](README.ru.md)
- [CHANGELOG.md](CHANGELOG.md) · [CONTRIBUTING.md](CONTRIBUTING.md) · [SECURITY.md](SECURITY.md) · [THREAT-MODEL.md](THREAT-MODEL.md) · [LICENSE](LICENSE)
