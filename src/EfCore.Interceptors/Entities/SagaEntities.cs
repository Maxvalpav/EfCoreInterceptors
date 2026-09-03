namespace EfCore.Interceptors.Entities;

/// <summary>Saga execution state (03.15).</summary>
public enum SagaState
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Compensated
}

/// <summary>
/// Durable saga instance (03.15): tracks which step a multi-transaction workflow reached,
/// so a crashed/restarted process resumes instead of repeating work. Map it
/// (<c>modelBuilder.Entity&lt;SagaInstance&gt;()</c>) in the coordinating database.
/// Steps themselves may span databases: each step owns its transaction, the instance row
/// is the single source of truth for progress.
/// </summary>
public class SagaInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Logical saga type (definition name).</summary>
    public string SagaType { get; set; } = string.Empty;

    /// <summary>Index of the next step to run.</summary>
    public int StepIndex { get; set; }

    public SagaState State { get; set; } = SagaState.Pending;

    /// <summary>Opaque saga payload (JSON, owned by the definition).</summary>
    public string? PayloadJson { get; set; }

    /// <summary>Last failure / compensation error.</summary>
    public string? Error { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
