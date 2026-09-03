# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 1.0.x   | :white_check_mark: |
| < 1.0.0 | :x:                |

## Reporting a Vulnerability

This library handles **encryption at rest (AES-GCM)**, tenant isolation, and SQL
governance guards. If you find a security issue — especially in

- `Abstractions/Encryption.cs` (AES-GCM payload, AAD binding, key handling),
- `Saving/PropertyEncryptionSaveChangesInterceptor.cs` / bulk encrypted updates,
- `Model/TenantModelCacheKeyFactory.cs` / tenant filters,
- `Commands/BulkOperationGuardInterceptor.cs` / governance guards,
- `Commands/CachingCommandInterceptor.cs` (cross-tenant cache isolation),

**do NOT open a public issue.** Instead:

1. Email the maintainers privately (see repository profile / NuGet package owners).
2. Include: affected version, reproduction steps or PoC, and impact assessment.

We aim to acknowledge within **3 business days** and ship a fix as soon as possible,
followed by a public advisory and a patched release.

## Scope Notes

- Key rotation: payload v1 carries a format version but no key id (`kid`); plan
  rotations as dump + re-encrypt maintenance windows until the v2 payload lands.
- Bulk paths (`ExecuteUpdate`/`ExecuteDelete`) bypass `ISaveChangesInterceptor` by EF
  design — use the `BulkExtensions.Execute*` safe helpers; raw bulk calls on
  `[Encrypted]` / soft-deletable / tenant-scoped entities are a known footgun
  guarded by `BulkOperationGuardInterceptor` and analyzer rules `EFI1001+`.
