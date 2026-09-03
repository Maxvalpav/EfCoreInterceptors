using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors.Sagas;

/// <summary>One saga step: forward action plus optional compensation (03.15).</summary>
public sealed class SagaStep
{
    public string Name { get; init; } = string.Empty;
    public Func<IServiceProvider, CancellationToken, Task> Execute { get; init; } =
        (_, _) => Task.CompletedTask;
    public Func<IServiceProvider, Exception, CancellationToken, Task>? Compensate { get; init; }
}

/// <summary>Ordered saga definition (choreography building block, 03.15).</summary>
public sealed class SagaDefinition
{
    public string Type { get; init; } = string.Empty;
    public IReadOnlyList<SagaStep> Steps { get; init; } = [];
}

/// <summary>Outcome of <see cref="SagaRunner.RunAsync"/>.</summary>
public sealed record SagaResult(
    bool Succeeded,
    int ExecutedSteps,
    int CompensatedSteps,
    Exception? Failure,
    string? CompensationError);

/// <summary>
/// Durable saga runner (03.15): executes steps in order, each in its own transaction
/// together with the <see cref="SagaInstance"/> progress row. On step failure completed
/// steps are compensated in reverse order and the instance parks as
/// <see cref="SagaState.Failed"/> (or <see cref="SagaState.Compensated"/>).
/// A restarted process calling <c>RunAsync</c> with the same id resumes at
/// <see cref="SagaInstance.StepIndex"/> instead of repeating work.
/// Steps must be idempotent (at-least-once): a crash between step commit and progress
/// commit re-runs the step. Cross-database sagas: resolve other DbContexts from the
/// service provider inside steps; propagate the saga id via the outbox.
/// </summary>
public static class SagaRunner
{
    public static async Task<SagaResult> RunAsync(
        IServiceProvider services,
        DbContext db,
        string sagaId,
        SagaDefinition definition,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(sagaId);
        ArgumentNullException.ThrowIfNull(definition);
        var time = clock ?? TimeProvider.System;

        var instance = await db.Set<SagaInstance>().FindAsync([sagaId], cancellationToken)
            .ConfigureAwait(false);
        if (instance is null)
        {
            instance = new SagaInstance
            {
                Id = sagaId, SagaType = definition.Type,
                StepIndex = 0, State = SagaState.InProgress, UpdatedAtUtc = time.GetUtcNow()
            };
            db.Set<SagaInstance>().Add(instance);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (instance.State is SagaState.Completed)
        {
            return new SagaResult(true, 0, 0, null, null);
        }
        else if (instance.State is SagaState.Failed or SagaState.Compensated)
        {
            return new SagaResult(false, 0, 0,
                instance.Error is null ? null : new InvalidOperationException(instance.Error), null);
        }
        else
        {
            instance.State = SagaState.InProgress;
            instance.UpdatedAtUtc = time.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var executed = 0;
        for (var i = instance.StepIndex; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            await using var tx = await db.Database
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await step.Execute(services, cancellationToken).ConfigureAwait(false);
                instance.StepIndex = i + 1;
                instance.UpdatedAtUtc = time.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                executed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try { await tx.RollbackAsync(cancellationToken).ConfigureAwait(false); } catch { }
                return await FailAsync(db, instance, definition, i, executed, services, ex, time, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        instance.State = SagaState.Completed;
        instance.UpdatedAtUtc = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SagaResult(true, executed, 0, null, null);
    }

    private static async Task<SagaResult> FailAsync(
        DbContext db, SagaInstance instance, SagaDefinition definition,
        int failedIndex, int executedBefore, IServiceProvider services,
        Exception failure, TimeProvider time, CancellationToken ct)
    {
        var compensated = 0;
        string? compensationError = null;
        // Compensate completed steps in reverse (best-effort, each in its own save).
        for (var i = failedIndex - 1; i >= 0; i--)
        {
            var compensate = definition.Steps[i].Compensate;
            if (compensate is null) continue;
            try
            {
                await compensate(services, failure, ct).ConfigureAwait(false);
                compensated++;
            }
            catch (Exception ex)
            {
                compensationError = $"Step '{definition.Steps[i].Name}': {ex.Message}";
                break; // stop at first compensation failure — operator resumes manually
            }
        }

        instance.State = compensationError is null ? SagaState.Compensated : SagaState.Failed;
        instance.Error = compensationError
            ?? $"Step '{definition.Steps[failedIndex].Name}' failed: {failure.Message}";
        instance.UpdatedAtUtc = time.GetUtcNow();
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); } catch { }
        return new SagaResult(false, executedBefore, compensated, failure, compensationError);
    }
}
