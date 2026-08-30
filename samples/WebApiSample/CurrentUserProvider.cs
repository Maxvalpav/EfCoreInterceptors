using EfCore.Interceptors.Abstractions;

namespace WebApiSample;

/// <summary>
/// Scoped current-user resolution: the audit interceptor is registered as SCOPED and receives this,
/// so every save is stamped with the caller from the X-User header (fallback: anonymous).
/// This is the correct DI pattern for interceptors that need per-request state.
/// </summary>
public class HttpContextCurrentUserProvider(IHttpContextAccessor accessor) : ICurrentUserProvider
{
    public string? UserName
        => accessor.HttpContext?.Request.Headers["X-User"].FirstOrDefault()
           ?? "anonymous";
}

public class StaticTenantProviderForApi : ITenantProvider
{
    public string CurrentTenantId => "demo-tenant";
}
