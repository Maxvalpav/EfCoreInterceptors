using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// What to do with the in-memory entity when a concurrency conflict is detected:
/// <list type="bullet">
///   <item><see cref="ConcurrencyRetryPolicy.ClientWins"/> — keep client values, move the
///     original-value baseline to current database state and retry ("last write wins").</item>
///   <item><see cref="ConcurrencyRetryPolicy.StoreWins"/> — reload database values into the
///     entity (client changes are discarded) and retry.</item>
/// </list>
/// </summary>
public enum ConcurrencyRetryPolicy
{
    ClientWins,
    StoreWins
}

/// <summary>
/// The classic "retry around SaveChanges" recipe implemented as an interceptor:
/// when EF is about to throw DbUpdateConcurrencyException, conflicting entries are reconciled
/// according to the policy, the save is re-executed and the original failure suppressed —
/// the caller just sees a successful SaveChanges. Retries are bounded by maxRetries with
/// exponential backoff; once spent, the conflict surfaces as usual.
/// Requires a mapped concurrency token (rowversion or IsConcurrencyToken()).
/// </summary>
public class ConcurrencyRetrySaveChangesInterceptor(
    ConcurrencyRetryPolicy policy = ConcurrencyRetryPolicy.ClientWins,
    int maxRetries = 3,
    TimeSpan? initialDelay = null,
    Action<int, TimeSpan>? onRetry = null) : SaveChangesInterceptor
{
    private static readonly ConditionalWeakTable<DbContext, RetryState> _state = new();
    public static void Clear(DbContext context) => _state.Remove(context);
    public static bool IsRetrying(DbContext context) => context is not null && _state.TryGetValue(context, out var s) && s.Retrying;
    private sealed class RetryState
    {
        public int Attempts;
        public bool Retrying;
    }

    private readonly ConcurrencyRetryPolicy _policy = policy;
    private readonly int _maxRetries = Math.Max(0, maxRetries);
    private readonly TimeSpan _initialDelay = initialDelay ?? TimeSpan.FromMilliseconds(100);
    private readonly Action<int, TimeSpan>? _onRetry = onRetry;

    // ---- budget lifecycle ---------------------------------------------------------------

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ResetBudget(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ResetBudget(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Cleanup(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Cleanup(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private RetryState GetState(DbContext context)
        => _state.GetOrCreateValue(context);

    private void ResetBudget(DbContext? context)
    {
        if (context is { } c && !IsNested(c))
        {
            GetState(c).Attempts = 0;
        }
    }

    private void Cleanup(DbContext? context)
    {
        if (context is { } c)
        {
            _state.Remove(c);
        }
    }

    private bool IsNested(DbContext context)
        => _state.TryGetValue(context, out var s) && s.Retrying;

    // ---- conflict interception -----------------------------------------------------------

    public override InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData, InterceptionResult result)
    {
        if (eventData.Context is not { } context || IsNested(context) || context.Database.CurrentTransaction is not null)
        {
            return base.ThrowingConcurrencyException(eventData, result);
        }

        DbUpdateConcurrencyException? failure = eventData.Exception;
        GetState(context).Attempts = 0;

        for (var retry = 1; retry <= _maxRetries; retry++)
        {
            if (failure is null)
            {
                break;
            }

            // Budget check: attempt 1 is the initial reconcile of the first conflict.
            if (retry > 1)
            {
                var delay = BackoffDelay(retry);
                _onRetry?.Invoke(retry, delay);

                if (delay > TimeSpan.Zero)
                {
                    Thread.Sleep(delay);
                }
            }

            Reconcile(failure);

            GetState(context).Retrying = true;
            try
            {
                Observability.SharedMeter.SaveChangesRetries.Add(1,
                    new KeyValuePair<string, object?>("policy", _policy.ToString()));
                context.SaveChanges();
                Cleanup(context);
                return InterceptionResult.Suppress(); // retried save committed — caller sees success
            }
            catch (DbUpdateConcurrencyException next)
            {
                failure = next; // still conflicting — loop to reconcile again
            }
            finally
            {
                if (_state.TryGetValue(context, out var st)) st.Retrying = false;
            }

            // Note: callers with nested SaveChanges (Version, ChangeLog, Encryption) should check ConcurrencyRetrySaveChangesInterceptor.IsRetrying(context) to avoid double effects
        }

        _state.Remove(context);

        // Budget spent: let EF raise the last conflict as usual.
        return base.ThrowingConcurrencyException(eventData, result);
    }

    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
        => DriveAsync(eventData, result, cancellationToken);

    private async ValueTask<InterceptionResult> DriveAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken)
    {
        if (eventData.Context is not { } context || IsNested(context) || context.Database.CurrentTransaction is not null)
        {
            return await base.ThrowingConcurrencyExceptionAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }

        DbUpdateConcurrencyException? failure = eventData.Exception;
        GetState(context).Attempts = 0;

        for (var retry = 1; retry <= _maxRetries; retry++)
        {
            if (failure is null)
            {
                break;
            }

            if (retry > 1)
            {
                var delay = BackoffDelay(retry);
                _onRetry?.Invoke(retry, delay);

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            await ReconcileAsync(failure, cancellationToken).ConfigureAwait(false);

            GetState(context).Retrying = true;
            try
            {
                Observability.SharedMeter.SaveChangesRetries.Add(1,
                    new KeyValuePair<string, object?>("policy", _policy.ToString()));
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                Cleanup(context);
                return InterceptionResult.Suppress();
            }
            catch (DbUpdateConcurrencyException next)
            {
                failure = next;
            }
            finally
            {
                if (_state.TryGetValue(context, out var st)) st.Retrying = false;
            }
        }

        _state.Remove(context);

        return await base.ThrowingConcurrencyExceptionAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    // ---- reconciliation -------------------------------------------------------------------

    private void Reconcile(DbUpdateConcurrencyException failure)
    {
        foreach (var entry in failure.Entries)
        {
            switch (_policy)
            {
                case ConcurrencyRetryPolicy.ClientWins:
                    {
                        var dbValues = entry.GetDatabaseValues()
                            ?? throw new InvalidOperationException(
                                $"Row for '{entry.Metadata.ClrType.Name}' was deleted by another party.");

                        entry.OriginalValues.SetValues(dbValues);
                        break;
                    }

                case ConcurrencyRetryPolicy.StoreWins:
                    {
                        // Single round-trip: Reload throws if row was deleted
                        try { entry.Reload(); }
                        catch (InvalidOperationException ex) when (ex.Message.Contains("deleted", StringComparison.OrdinalIgnoreCase))
                        { throw new InvalidOperationException($"Row for '{entry.Metadata.ClrType.Name}' was deleted by another party.", ex); }
                        break;
                    }
            }
        }
    }

    private async Task ReconcileAsync(DbUpdateConcurrencyException failure, CancellationToken ct)
    {
        foreach (var entry in failure.Entries)
        {
            switch (_policy)
            {
                case ConcurrencyRetryPolicy.ClientWins:
                    {
                        var dbValues = await entry.GetDatabaseValuesAsync(ct).ConfigureAwait(false)
                            ?? throw new InvalidOperationException($"Row for '{entry.Metadata.ClrType.Name}' was deleted by another party.");
                        entry.OriginalValues.SetValues(dbValues);
                        break;
                    }
                case ConcurrencyRetryPolicy.StoreWins:
                    {
                        await entry.ReloadAsync(ct).ConfigureAwait(false);
                        break;
                    }
            }
        }
    }

    private TimeSpan BackoffDelay(int retryNumber)
    {
        var ms = _initialDelay.TotalMilliseconds * Math.Pow(2, retryNumber - 1);
        ms = Math.Min(ms, 5000);
        if (double.IsInfinity(ms) || ms > TimeSpan.MaxValue.TotalMilliseconds) ms = 5000;
        // jitter 80-120% to avoid thundering herd
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        ms *= jitter;
        return TimeSpan.FromMilliseconds(ms);
    }
}
