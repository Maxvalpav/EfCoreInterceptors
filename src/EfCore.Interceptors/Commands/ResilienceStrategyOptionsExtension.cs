using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Options extension that registers <see cref="ResilienceExecutionStrategy"/> as the <see cref="IExecutionStrategyFactory"/>.
/// No external dependencies (Polly).
/// </summary>
public sealed class ResilienceStrategyOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public int MaxRetryCount { get; }
    public TimeSpan MaxRetryDelay { get; }
    public Func<Exception, bool>? IsTransient { get; }

    public ResilienceStrategyOptionsExtension(int maxRetryCount = 5, TimeSpan? maxRetryDelay = null, Func<Exception, bool>? isTransient = null)
    {
        MaxRetryCount = Math.Max(0, maxRetryCount);
        MaxRetryDelay = maxRetryDelay ?? TimeSpan.FromSeconds(30);
        IsTransient = isTransient;
    }

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        services.AddScoped<IExecutionStrategyFactory>(sp =>
        {
            var deps = sp.GetRequiredService<ExecutionStrategyDependencies>();
            return new ResilienceExecutionStrategyFactory(deps, MaxRetryCount, MaxRetryDelay, IsTransient);
        });
    }

    public void Validate(IDbContextOptions options) { }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(IDbContextOptionsExtension extension) : base(extension) { }
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => $"Resilience retries={((ResilienceStrategyOptionsExtension)Extension).MaxRetryCount} ";
        public override int GetServiceProviderHashCode() => ((ResilienceStrategyOptionsExtension)Extension).MaxRetryCount.GetHashCode() ^ ((ResilienceStrategyOptionsExtension)Extension).MaxRetryDelay.GetHashCode();
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo e && e.Extension is ResilienceStrategyOptionsExtension o && o.MaxRetryCount == ((ResilienceStrategyOptionsExtension)Extension).MaxRetryCount && o.MaxRetryDelay == ((ResilienceStrategyOptionsExtension)Extension).MaxRetryDelay;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["Resilience:MaxRetryCount"] = ((ResilienceStrategyOptionsExtension)Extension).MaxRetryCount.ToString();
    }
}
