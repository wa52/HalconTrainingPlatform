namespace HalconTraining.Domain.Training;

public enum CapabilityStatus
{
    Passed,
    Failed,
    Unsupported,
    Unknown,
    NotRun
}

public sealed record TrainingTaskCapability(TaskKind Task, CapabilityStatus Status, string Summary, string? Evidence = null);
