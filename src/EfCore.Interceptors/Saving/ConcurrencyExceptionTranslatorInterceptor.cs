using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Translates EF's low-level <c>DbUpdateConcurrencyException</c> into the domain-friendly
/// <see cref="ConcurrencyConflictException"/> so upper layers don't reference EF exceptions.
/// </summary>
public class ConcurrencyExceptionTranslatorInterceptor : SaveChangesInterceptor
{
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Exception is DbUpdateConcurrencyException concurrencyFailure)
        {
            throw new ConcurrencyConflictException(
                "The record was modified by another user. Reload and retry.",
                concurrencyFailure);
        }

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Exception is DbUpdateConcurrencyException concurrencyFailure)
        {
            throw new ConcurrencyConflictException(
                "The record was modified by another user. Reload and retry.",
                concurrencyFailure);
        }

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }
}
