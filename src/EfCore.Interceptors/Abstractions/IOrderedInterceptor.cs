namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Allows deterministic ordering of SaveChanges interceptors.
/// Lower Order executes first (Validation → Guards → MultiTenancy → SoftDelete → Audit → Version → ChangeLog → Outbox → DomainEvents → Metrics).
/// Interceptors not implementing this default to 0.
/// </summary>
public interface IOrderedInterceptor
{
    int Order { get; }
}
