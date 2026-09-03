# Threat model — EfCore.Interceptors

> Answers the S-1 question: **who** we protect against, **what** is (and is not) guaranteed.
> Assumptions here are enforced by tests where possible (`TenantStrictIsolationTests`,
> `ChangeLogEncryptionTests`, `ArchitectureTests.SaveChanges_Interceptors_Follow_Order_Contract`).

## 1. Actors and trust boundaries

| Actor | Trust | Examples |
|---|---|---|
| App developer (first party) | **Partially trusted** — can make mistakes, must be stopped from shooting their own foot | `IgnoreQueryFilters()` by accident, `ExecuteDelete` on soft-deletable entities, plaintext write into `[Encrypted]` |
| Malicious co-tenant (end user) | **Untrusted** — only ever sees their own `TenantId` | sends crafted requests, reuses IDs, guesses keys |
| DBA / backup operator | **Trusted for availability, untrusted for content secrecy** | reads raw tables, restores backups |
| Log/metrics pipeline | **Untrusted for PII/secrets** | SQL logs, traces, dashboards, error reports |
| App process itself | **Trusted while intact** — a compromised process defeats everything below | memory dumps, debugger attach |
| NuGet consumers (supply chain) | **Untrusted input, trusted transport** | typosquatting is out of scope; signed builds + SBOM are in scope |

**Global non-guarantee:** client-side interception cannot protect against a compromised
application process (memory dump reveals keys and plaintext) or a malicious DBA with
`UPDATE` rights (they can flip `IsDeleted`/`TenantId` directly — use native RLS + grants).

## 2. Per-feature analysis (STRIDE)

### 2.1. Multi-tenancy (`MultiTenancySaveChangesInterceptor` + query filters)

- **Guarantees:** `TenantId` stamped on insert; modification rejected (`CrossTenantAccessException`,
  enforced via `OriginalValue` immutability); `WithMultiTenancy()` installs a strict query policy
  (`IgnoreQueryFilters` forbidden) by default, opt-out via `strictQueryPolicy: false`.
- **Does NOT guarantee:** isolation against `FromSqlRaw`/`ExecuteSqlRaw` (bypasses filters —
  lock behind `WithStrictQueryPolicy(forbidUntaggedRawSql: true)` + `TagWith("tenant-safe")`
  allow-list), against `ExecuteUpdate().SetProperty(e => e.TenantId, …)` (use
  `WithBulkOperationGuard` + analyzers EFI1001–EFI1005), or against a DBA editing rows.
- **Real isolation** is only database-level: schema-per-tenant, separate databases
  (`WithDynamicConnectionString`), or native RLS (Postgres `app.tenant_id` / SQL Server
  security policies). The library is **defense in depth**, never the sole boundary.
- **Spoofing:** tenant identity comes from `ITenantProvider` — the app must authenticate
  the caller before resolving the tenant. An unauthenticated provider is a tenant-confusion hole.

### 2.2. Row-level security (`ApplyRowLevelSecurity`, `ElevatedSession`)

- Executing-context substitution: predicates are evaluated per query, nothing is baked into
  cached query plans (proven by test: switching context changes results on the same
  compiled query). Elevated sessions are audited (`ef.rls.elevated`).
- Same non-guarantees as 2.1: no protection against raw SQL or direct DB access.

### 2.3. Property encryption (`[Encrypted]`, AES-GCM, key ring v2)

- **Guarantees:** AEAD (AES-GCM) with versioned payload (`0x01…` / `0x02|kid|…`);
  AAD `table|column|pk` (opt-in path) defeats cross-column ciphertext swaps;
  double-encryption and non-`[Encrypted]` bulk writes are refused fail-closed;
  `ChangeLog` stores `[encrypted]` markers, never plaintext nor ciphertext (S-5).
- **Does NOT guarantee:** searchability (random 96-bit nonce — equality/LIKE impossible;
  blind indexes are future work), security past ~2³² encryptions under one key
  (NIST SP 800-38D — rotate via `ReEncryptAsync`), secrecy from the app process
  (keys live in managed memory; prefer envelope encryption + KMS in production),
  secrecy from backups unless the KEK is stored separately.
- **Key management:** `IKeyRing` + `KeyId`; `AllowLegacyV1` gate for migration posture;
  `DecryptionFailed` hook for lenient reads. Missing-key behavior is fail-closed.

### 2.4. Audit trail (`ChangeLog`) and outbox

- `ChangeLog`/`OutboxMessage` rows are written in the **same transaction** as the business
  change (atomicity). Outbox delivery is at-least-once with claim tokens (no cross-instance
  double processing), backoff with jitter, DLQ after `maxAttempts`.
- **Deserialization boundary:** `IOutboxTypeResolver` must be an allow-list of types
  registered via `AddOutboxDispatcher` — never `Type.GetType(untrusted string)` on payloads
  from the DB (a compromised database would otherwise gain code-loading influence).
- `ChangeLog` JSON may contain business PII by design — treat the table (and the Dashboard
  changelog viewer) as sensitive; use `[ExcludeFromChangeLog]` for secrets.

### 2.5. SQL logging / observability

- `WithSqlLogging()` without a `textRedactor` writes parameter values to logs.
  Default posture: sample in production, always attach `PiiRedactor.Default`,
  never ship parameter values to third-party sinks. Connection strings are masked
  (`Password/Pwd/Secret/Token`); `\r\n` is sanitized against log injection.

### 2.6. Dashboard (`MapEfInterceptorsDashboard`)

- Mutating endpoints (outbox retry/purge) and PII-bearing views (changelog, cache panel)
  require explicit authorization: always chain `RequireAuthorization()`, serve behind
  CSRF-protected admin routes, set CSP headers for the embedded HTML, and mask
  `[Encrypted]`/classified fields in the UI. The dashboard is an **opt-in admin surface**,
  never a public endpoint.

### 2.7. Second-level cache

- Cache key = query hash + parameters; deployments with per-tenant databases
  (`WithDynamicConnectionString`) must use isolated stores per database — a shared
  in-memory store across databases serves cross-database rows.
- Cache invalidation from SQL text parsing is best-effort; unparseable writes fail safe
  to full clear. Size/row limits prevent single-query memory exhaustion.

### 2.8. GDPR (`ForgetSubjectAsync`) and data classification

- The library provides **tooling** (salted-SHA-256 pseudonymization, erasure helpers,
  `[SubjectIdentifier]` discovery, catalog export) — not a compliance guarantee.
  Right-to-erasure ≠ pseudonymization; backups, outbox payloads and `ChangeLog` history
  need a separate retention story (`ChangeLogMaintenance.DeleteOlderThanAsync`).

## 3. Supply chain

- Dependencies of the core package are EF Core + `Microsoft.Extensions.*` +
  `System.IO.Hashing` only; messaging/cache/OTel live in satellite packages.
- CI pins: Dependabot covers NuGet + GitHub Actions; releases publish SBOM (SPDX);
  prefer OIDC trusted publishing over long-lived `NUGET_API_KEY`.
- Known advisory class: transitive `OpenTelemetry.Api` CVEs are tracked via Dependabot;
  upgrade cadence is part of the minor-release checklist.

## 4. Test-environment assumptions (not production guarantees)

- Tests build dozens of `DbContextOptions` variants against shared in-memory SQLite.
  EF8 shares compiled-query compilations across option variants in-process, so raw-SQL
  policy tests use unique SQL text per case (see comment in `TenantStrictIsolationTests`).
  Production builds options once — interceptors are app-wide singletons there.
- SQLite is a logic stand-in, not a provider proof: provider-specific paths
  (`SKIP LOCKED` claims, hints, isolation levels) are covered by Testcontainers
  integration tests (`Category=Integration`, PostgreSQL).
