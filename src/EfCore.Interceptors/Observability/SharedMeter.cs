using System.Diagnostics.Metrics;

namespace EfCore.Interceptors.Observability;

internal static class SharedMeter
{
    public static readonly Meter Meter = new("EfCore.Interceptors", "1.0.0");
}
