namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// EF Core calls this when <c>AddDbContextPool</c> returns a DbContext to the pool.
/// Implement it on your DbContext to avoid state leakage between requests (provider-matrix 2.1).
/// Example: <c>public class AppDbContext : DbContext, IResettableService { public void ResetState() => PooledContextHelper.Clear(this); }</c>
/// </summary>
public interface IResettableService
{
    void ResetState();
    Task ResetStateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Helper to clear per-DbContext state held by interceptors when pooling (<c>AddDbContextPool</c>).
/// Call from your DbContext's <see cref="IResettableService"/> implementation.
/// </summary>
public static class PooledContextHelper
{
    public static void Clear(Microsoft.EntityFrameworkCore.DbContext context)
    {
        // Each interceptor holds ConditionalWeakTable, but pool keeps DbContext alive — explicit clear is required.
        // We clear via static registries if interceptors are registered; no-op if not used.
        Saving.DomainEventsSaveChangesInterceptor.Clear(context);
        Saving.OutboxSaveChangesInterceptor.Clear(context);
        Saving.ChangeLogSaveChangesInterceptor.Clear(context);
        Saving.ConcurrencyRetrySaveChangesInterceptor.Clear(context);
        Commands.NPlusOneDetectorCommandInterceptor.Clear(context);
        Commands.CachingCommandInterceptor.Clear(context);
        Observability.LongRunningTransactionDetector.Clear(context);
    }

    public static Task ClearAsync(Microsoft.EntityFrameworkCore.DbContext context, CancellationToken ct = default)
    {
        Clear(context);
        return Task.CompletedTask;
    }
}
