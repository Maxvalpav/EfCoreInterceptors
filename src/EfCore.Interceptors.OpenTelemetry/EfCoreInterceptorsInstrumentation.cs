using OpenTelemetry.Trace;

namespace EfCore.Interceptors.OpenTelemetry;

/// <summary>
/// OpenTelemetry wiring (03.11): registers every ActivitySource the library emits —
/// <c>EfCore.Interceptors</c> (save/command spans via <c>WithTracing</c>) and
/// <c>EfCore.Interceptors.Outbox</c> (per-message spans from the processor).
/// Usage: <c>builder.AddEfCoreInterceptorsInstrumentation()</c> inside
/// <c>AddOpenTelemetry().WithTracing(...)</c>. Spans follow OTel semconv
/// (<c>db.system</c>, <c>db.statement</c>, <c>messaging.*</c>).
/// </summary>
public static class EfCoreInterceptorsInstrumentationExtensions
{
    /// <summary>ActivitySource names emitted by the library.</summary>
    public static readonly string[] ActivitySourceNames =
    [
        "EfCore.Interceptors",
        "EfCore.Interceptors.Outbox"
    ];

    public static TracerProviderBuilder AddEfCoreInterceptorsInstrumentation(
        this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(ActivitySourceNames);
    }
}
