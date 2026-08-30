using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<DbContext, int> _attempts = new();
    private readonly ConcurrentDictionary<DbContext, bool> _retrying = new();

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

    private void ResetBudget(DbContext? context)
    {
        if (context is { } c && !IsNested(c))
        {
            _attempts[c] = 0;
        }
    }

    private void Cleanup(DbContext? context)
    {
        if (context is { } c)
        {
            _attempts.TryRemove(c, out _);
            _retrying.TryRemove(c, out _);
        }
    }

    private bool IsNested(DbContext context)
        => _retrying.TryGetValue(context, out var retrying) && retrying;

    // ---- conflict interception -----------------------------------------------------------

    public override InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData, InterceptionResult result)
    {
        if (eventData.Context is not { } context || IsNested(context))
        {
            return base.ThrowingConcurrencyException(eventData, result);
        }

        DbUpdateConcurrencyException? failure = eventData.Exception;
        _attempts[context] = 0;

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

            _retrying[context] = true;
            try
            {
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
                _retrying[context] = false;
            }
        }

        _attempts.TryRemove(context, out _);
        _retrying.TryRemove(context, out _);

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
        if (eventData.Context is not { } context || IsNested(context))
        {
            return await base.ThrowingConcurrencyExceptionAsync(eventData, result, cancellationToken);
        }

        DbUpdateConcurrencyException? failure = eventData.Exception;
        _attempts[context] = 0;

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
                    await Task.Delay(delay, cancellationToken);
                }
            }

            Reconcile(failure);

            _retrying[context] = true;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                Cleanup(context);
                return InterceptionResult.Suppress();
            }
            catch (DbUpdateConcurrencyException next)
            {
                failure = next;
            }
            finally
            {
                _retrying[context] = false;
            }
        }

        _attempts.TryRemove(context, out _);
        _retrying.TryRemove(context, out _);

        return await base.ThrowingConcurrencyExceptionAsync(eventData, result, cancellationToken);
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
                    entry.Reload();
                    break;
            }
        }
    }

    private TimeSpan BackoffDelay(int retryNumber)
        => TimeSpan.FromMilliseconds(_initialDelay.TotalMilliseconds * Math.Pow(2, retryNumber - 1));
}
