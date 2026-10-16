namespace HalconTraining.Domain.Training;

public enum TrainingJobState
{
    Created,
    Queued,
    Preparing,
    Training,
    Validating,
    CancelRequested,
    Completed,
    Cancelled,
    Failed,
    Interrupted
}

public sealed record TrainingJobSnapshot(
    string JobId,
    TrainingJobState State,
    long Sequence,
    DateTimeOffset UpdatedAtUtc,
    string? Message = null,
    double? Progress = null)
{
    public bool IsTerminal => State is TrainingJobState.Completed or TrainingJobState.Cancelled or TrainingJobState.Failed or TrainingJobState.Interrupted;
}
