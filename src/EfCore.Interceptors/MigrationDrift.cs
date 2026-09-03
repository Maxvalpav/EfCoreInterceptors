using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfCore.Interceptors;

/// <summary>Reaction to migration drift (03.8).</summary>
public enum MigrationDriftPolicy
{
    Warn,
    Throw
}

/// <summary>
/// Migration drift detector (03.8): fails fast at startup when the database's
/// <c>__EFMigrationsHistory</c> does not match the migrations compiled into the app
/// (someone migrated by hand, or the deploy shipped stale binaries).
/// Call once at startup: <c>MigrationDrift.EnsureNoDrift(db)</c>.
/// </summary>
public static class MigrationDrift
{
    /// <returns>(missingFromDb, extraInDb) migration ids.</returns>
    public static (IReadOnlyList<string> MissingFromDb, IReadOnlyList<string> ExtraInDb) Detect(DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        IReadOnlyList<string> applied;
        try { applied = db.Database.GetAppliedMigrations().ToList(); }
        catch (Exception)
        {
            // Non-relational provider, missing history table, or no migrations
            // infrastructure — nothing to compare (best-effort startup probe).
            return ([], []);
        }
        IReadOnlyList<string> defined;
        try { defined = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList(); }
        catch (InvalidOperationException) { return ([], []); }

        var appliedSet = new HashSet<string>(applied, StringComparer.Ordinal);
        var definedSet = new HashSet<string>(defined, StringComparer.Ordinal);
        return ([.. definedSet.Where(m => !appliedSet.Contains(m))],
                [.. appliedSet.Where(m => !definedSet.Contains(m))]);
    }

    public static void EnsureNoDrift(DbContext db, MigrationDriftPolicy policy = MigrationDriftPolicy.Throw)
    {
        var (missing, extra) = Detect(db);
        if (missing.Count == 0 && extra.Count == 0) return;
        var message = $"Migration drift detected. Missing from DB: [{string.Join(", ", missing)}]. " +
                      $"Extra in DB (not in assembly): [{string.Join(", ", extra)}].";
        if (policy == MigrationDriftPolicy.Throw)
            throw new MigrationDriftException(message);
        System.Diagnostics.Trace.WriteLine("[EfCore.Interceptors] Warning: " + message);
    }
}

/// <summary>Database migration history diverges from the compiled migrations.</summary>
public sealed class MigrationDriftException(string message) : InvalidOperationException(message);
