# Changelog

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
