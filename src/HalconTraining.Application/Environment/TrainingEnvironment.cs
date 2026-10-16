namespace HalconTraining.Application.Environment;

public sealed record EnvironmentCheck(string Name, Domain.Training.CapabilityStatus Status, string Summary, string? Details = null);

public sealed record TrainingEnvironmentReport(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string ProcessArchitecture,
    string DotnetRuntime,
    string? HalconRoot,
    string? HalconArchitecture,
    string? HalconVersion,
    string? HalconManagedAssembly,
    string? GpuName,
    string? GpuDriver,
    long? GpuMemoryBytes,
    IReadOnlyList<string> HalconDlDevices,
    IReadOnlyList<EnvironmentCheck> Checks,
    IReadOnlyList<Domain.Training.TrainingTaskCapability> Tasks)
{
    public bool CanUseHalconRuntime => Checks.Any(check => check.Name == "HALCON runtime" && check.Status == Domain.Training.CapabilityStatus.Passed);

    public bool HasSupportedTrainingDevice =>
        Checks.Any(check => check.Name == "HALCON GPU support" && check.Status == Domain.Training.CapabilityStatus.Passed) &&
        Checks.Any(check => check.Name == "HALCON DL GPU 设备" && check.Status == Domain.Training.CapabilityStatus.Passed);
}

public interface ITrainingEnvironmentProbe
{
    Task<TrainingEnvironmentReport> CaptureAsync(CancellationToken cancellationToken = default);
}

public interface ITrainingEnvironmentReportStore
{
    Task SaveAsync(TrainingEnvironmentReport report, string outputPath, CancellationToken cancellationToken = default);
}
