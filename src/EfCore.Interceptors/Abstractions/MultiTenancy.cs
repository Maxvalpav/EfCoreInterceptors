namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Implemented by entities that belong to a tenant. Pair with
/// <see cref="Saving.MultiTenancySaveChangesInterceptor"/> to stamp TenantId on insert and
/// reject cross-tenant modifications.
/// </summary>
public interface ITenantEntity
{
    /// <summary>Tenant identifier (string keeps it provider-agnostic: int, Guid or code).</summary>
    string? TenantId { get; set; }
}

/// <summary>Resolves the current tenant from ambient context (HTTP header, JWT claim, etc.).</summary>
public interface ITenantProvider
{
    string? CurrentTenantId { get; }
}

/// <summary>Fixed tenant source — handy for tests and single-tenant deployments.</summary>
public sealed class StaticTenantProvider(string tenantId) : ITenantProvider
{
    public string CurrentTenantId => tenantId;
}

/// <summary>Raised when an entity is modified under a different tenant than its own.</summary>
public sealed class CrossTenantAccessException(string message) : InvalidOperationException(message);

/// <summary>Raised when a single SaveChanges would touch more rows than allowed.</summary>
public sealed class MassOperationException(string message) : InvalidOperationException(message);

/// <summary>Raised instead of EF's DbUpdateConcurrencyException by ConcurrencyExceptionTranslatorInterceptor.</summary>
public sealed class ConcurrencyConflictException(string message, Exception inner) : InvalidOperationException(message, inner);

/// <summary>Raised by StrictQueryPolicyQueryExpressionInterceptor for forbidden query shapes.</summary>
public sealed class QueryPolicyViolationException(string message) : InvalidOperationException(message);

/// <summary>Raised by WritesRequireTransactionCommandInterceptor when a write runs outside an explicit transaction.</summary>
public sealed class MissingTransactionException(string message) : InvalidOperationException(message);
