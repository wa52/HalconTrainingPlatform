using System.Text.Json;
using System.Text.Json.Serialization;
using HalconTraining.Application.Environment;

namespace HalconTraining.Application.Workers;

public sealed record WorkerMessage(
    int SchemaVersion,
    string JobId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    string Kind,
    JsonElement Payload)
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static WorkerMessage EnvironmentProbeCompleted(string jobId, TrainingEnvironmentReport report) =>
        new(1, jobId, 1, DateTimeOffset.UtcNow, "EnvironmentProbeCompleted", JsonSerializer.SerializeToElement(report, PayloadOptions));
}

public sealed record WorkerFailure(string ErrorType, string Message);

public interface ITrainingWorkerClient
{
    Task<TrainingEnvironmentReport> CaptureEnvironmentAsync(CancellationToken cancellationToken = default);
}
