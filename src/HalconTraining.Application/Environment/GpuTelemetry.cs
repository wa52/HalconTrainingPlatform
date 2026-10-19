namespace HalconTraining.Application.Environment;

public sealed record GpuTelemetryDevice(int Index, string Name, double? UtilizationPercent,
    double? UsedMemoryMiB, double? TotalMemoryMiB, double? TemperatureCelsius, double? PowerWatts);

public sealed record GpuTelemetryReport(DateTimeOffset CapturedAt, IReadOnlyList<GpuTelemetryDevice> Devices, string? Error = null);

/// <summary>Live hardware telemetry, independent of HALCON license or task compatibility checks.</summary>
public interface IGpuTelemetry
{
    Task<GpuTelemetryReport> CaptureAsync(CancellationToken cancellationToken = default);
}
