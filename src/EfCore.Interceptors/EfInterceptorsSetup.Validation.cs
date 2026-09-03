using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors;

/// <summary>Validation entry point on the fluent setup (I-1).</summary>
public sealed partial class EfInterceptorsSetup
{
    /// <summary>
    /// Validates this setup against the model of <paramref name="context"/>:
    /// soft-delete filters, ChangeLog/Outbox mapping, duplicates, provider fit.
    /// </summary>
    public IReadOnlyList<InterceptorValidationIssue> Validate(DbContext context)
        => EfInterceptorsValidator.Validate(_interceptors, context);

    /// <summary>Validates and throws on the first error-level finding.</summary>
    public IReadOnlyList<InterceptorValidationIssue> ValidateAndThrow(DbContext context)
        => EfInterceptorsValidator.ValidateAndThrow(_interceptors, context);
}
