using System.Diagnostics.Metrics;
using EfCore.Interceptors.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Explicit privilege escalation for row-level security (03.2): inside the scope
/// RLS query filters pass every row and the SaveChanges guard is bypassed.
/// Every use is logged (Warning) and counted (<c>ef.rls.elevated</c>) — the
/// security audit trail. Keep scopes tight and never flow them into user code.
/// </summary>
public sealed class ElevatedSession : IDisposable
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly Counter<long> Elevations =
        SharedMeter.Meter.CreateCounter<long>("ef.rls.elevated");

    private sealed class Scope(string? reason, ILogger logger)
    {
        public string? Reason { get; } = reason;
        public ILogger Logger { get; } = logger;
    }

    private readonly Scope? _parent;
    private bool _disposed;

    private ElevatedSession(string? reason, ILoggerFactory? loggerFactory)
    {
        _parent = Current.Value;
        var logger = loggerFactory?.CreateLogger("EfCore.Interceptors.Rls") ?? NullLogger.Instance;
        Current.Value = new Scope(reason, logger);
        Elevations.Add(1);
        logger.LogWarning("Row-level security elevated{Reason}. Remember to dispose the scope.",
            reason is null ? string.Empty : $" (reason: {reason})");
    }

    /// <summary>Whether the current async flow runs elevated.</summary>
    public static bool IsElevated => Current.Value is not null;

    /// <summary>Enters elevation; dispose to leave it. Nesting restores the parent.</summary>
    public static ElevatedSession Elevate(string? reason = null, ILoggerFactory? loggerFactory = null)
        => new(reason, loggerFactory);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Current.Value = _parent;
    }
}

/// <summary>Raised when a write violates the row-level security predicate.</summary>
public sealed class RowLevelSecurityException(string message) : UnauthorizedAccessException(message);
