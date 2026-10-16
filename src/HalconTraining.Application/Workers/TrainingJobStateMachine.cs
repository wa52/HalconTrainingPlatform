using HalconTraining.Domain.Training;

namespace HalconTraining.Application.Workers;

public sealed class TrainingJobStateMachine
{
    private static readonly IReadOnlyDictionary<TrainingJobState, TrainingJobState[]> Transitions =
        new Dictionary<TrainingJobState, TrainingJobState[]>
        {
            [TrainingJobState.Created] = [TrainingJobState.Queued, TrainingJobState.Cancelled, TrainingJobState.Failed],
            [TrainingJobState.Queued] = [TrainingJobState.Preparing, TrainingJobState.CancelRequested, TrainingJobState.Failed],
            [TrainingJobState.Preparing] = [TrainingJobState.Training, TrainingJobState.Validating, TrainingJobState.CancelRequested, TrainingJobState.Failed, TrainingJobState.Interrupted],
            [TrainingJobState.Training] = [TrainingJobState.Validating, TrainingJobState.CancelRequested, TrainingJobState.Failed, TrainingJobState.Interrupted],
            [TrainingJobState.Validating] = [TrainingJobState.Completed, TrainingJobState.CancelRequested, TrainingJobState.Failed, TrainingJobState.Interrupted],
            [TrainingJobState.CancelRequested] = [TrainingJobState.Cancelled, TrainingJobState.Failed, TrainingJobState.Interrupted],
            [TrainingJobState.Completed] = [],
            [TrainingJobState.Cancelled] = [],
            [TrainingJobState.Failed] = [],
            [TrainingJobState.Interrupted] = []
        };

    public TrainingJobSnapshot Create(string jobId, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return new TrainingJobSnapshot(jobId, TrainingJobState.Created, 0, DateTimeOffset.UtcNow, message);
    }

    public TrainingJobSnapshot Transition(TrainingJobSnapshot current, TrainingJobState next, string? message = null, double? progress = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.IsTerminal)
            throw new InvalidOperationException($"作业 {current.JobId} 已结束，状态 {current.State} 不可继续变更。");
        if (!Transitions[current.State].Contains(next))
            throw new InvalidOperationException($"作业状态不能从 {current.State} 转换为 {next}。");
        if (progress is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(progress), "进度必须在 0 到 1 之间。");
        if (next == TrainingJobState.Completed && progress is not null and < 1)
            throw new InvalidOperationException("已完成作业的进度必须为 100%。");

        return new TrainingJobSnapshot(current.JobId, next, current.Sequence + 1, DateTimeOffset.UtcNow, message, progress);
    }
}
