using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HalconDotNet;
using HalconTraining.Application.Environment;
using HalconTraining.Domain.Training;

namespace HalconTraining.Halcon.Devices;

/// <summary>Collects local facts; only a real HALCON model-training run can establish task readiness.</summary>
public sealed class HalconTrainingEnvironmentProbe : ITrainingEnvironmentProbe
{
    private const double MinSupportedComputeCapability = 5.0;
    private const double MaxSupportedComputeCapability = 9.0;

    public async Task<TrainingEnvironmentReport> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var halconRoot = System.Environment.GetEnvironmentVariable("HALCONROOT");
        var halconArch = System.Environment.GetEnvironmentVariable("HALCONARCH");
        var halconVersion = ReadVersion(halconRoot);
        var gpu = await ReadGpuAsync(cancellationToken).ConfigureAwait(false);
        var dlDevices = QueryDlDevices();
        var checks = new List<EnvironmentCheck>
        {
            RuntimeArchitectureCheck(),
            HalconInstallationCheck(halconRoot, halconArch, halconVersion),
            ReadHalconRuntimeCheck(),
            GpuAvailabilityCheck(gpu),
            GpuSupportCheck(gpu),
            DlDeviceCheck(dlDevices),
            new("训练许可证", CapabilityStatus.Unknown,
                "HALCON 深度学习训练许可状态须通过实际授权算子调用确认。",
                "环境变量、许可证文件或设备枚举都不能单独证明训练许可有效。"),
            new("各任务训练与推理", CapabilityStatus.NotRun,
                "尚未运行金标准数据集训练与推理。", "每种任务必须单独完成训练、评估、导出和重载验收。")
        };

        return new TrainingEnvironmentReport(
            DateTimeOffset.UtcNow,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            halconRoot,
            halconArch,
            halconVersion,
            typeof(HSystem).Assembly.Location,
            gpu?.Name,
            gpu?.Driver,
            gpu?.MemoryBytes,
            dlDevices.Names,
            checks,
            Enum.GetValues<TaskKind>()
                .Select(task => new TrainingTaskCapability(task, CapabilityStatus.NotRun,
                    "尚未使用金标准数据运行此任务的训练与推理。"))
                .ToArray());
    }

    private static EnvironmentCheck RuntimeArchitectureCheck()
    {
        var isSupported = OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        return new EnvironmentCheck("进程平台", isSupported ? CapabilityStatus.Passed : CapabilityStatus.Failed,
            isSupported ? "Windows x64 进程。" : "平台要求 Windows x64。",
            $"OS={RuntimeInformation.OSDescription}; process={RuntimeInformation.ProcessArchitecture}; pointer={IntPtr.Size * 8}-bit");
    }

    private static EnvironmentCheck HalconInstallationCheck(string? root, string? architecture, string? version)
    {
        var versionFile = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, "version.txt");
        var nativeRuntime = string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(architecture)
            ? null
            : Path.Combine(root, "bin", architecture, "halcon.dll");
        var ready = !string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(nativeRuntime) && File.Exists(nativeRuntime);
        var details = $"HALCONROOT={root ?? "<未设置>"}; HALCONARCH={architecture ?? "<未设置>"}; version.txt={versionFile ?? "<未知>"}; halcon.dll={nativeRuntime ?? "<未知>"}";
        return new EnvironmentCheck("HALCON 安装", ready ? CapabilityStatus.Passed : CapabilityStatus.Failed,
            ready ? $"发现 HALCON {version}。" : "未找到完整 HALCON 安装。", details);
    }

    private static EnvironmentCheck ReadHalconRuntimeCheck()
    {
        try
        {
            using var version = HSystem.GetSystem("version");
            return new EnvironmentCheck("HALCON runtime", CapabilityStatus.Passed,
                $"HALCON 原生运行时可调用，版本 {version.S}. ", typeof(HSystem).Assembly.Location);
        }
        catch (Exception exception)
        {
            return new EnvironmentCheck("HALCON runtime", CapabilityStatus.Failed,
                "HALCON 原生运行时加载或初始化失败。", $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static EnvironmentCheck GpuAvailabilityCheck(GpuInfo? gpu) => gpu is null
        ? new EnvironmentCheck("NVIDIA GPU", CapabilityStatus.Unknown, "未能从 nvidia-smi 获取 GPU 信息。", "检查 NVIDIA 驱动与 nvidia-smi 路径。")
        : new EnvironmentCheck("NVIDIA GPU", CapabilityStatus.Passed, gpu.Name,
            $"Driver={gpu.Driver}; total memory={FormatBytes(gpu.MemoryBytes)}; available memory={FormatBytes(gpu.FreeMemoryBytes)}");

    private static EnvironmentCheck GpuSupportCheck(GpuInfo? gpu)
    {
        if (gpu is null)
            return new EnvironmentCheck("HALCON GPU support", CapabilityStatus.Unknown, "缺少 GPU 型号，无法判断 HALCON 24.11 的公开兼容性范围。");

        if (gpu.ComputeCapability is null)
            return new EnvironmentCheck("HALCON GPU support", CapabilityStatus.Unknown,
                "GPU 型号不在本地已知型号映射中，需核对 NVIDIA 官方计算能力表。", gpu.Name);

        var capability = gpu.ComputeCapability.Value;
        var supported = capability >= MinSupportedComputeCapability && capability <= MaxSupportedComputeCapability;
        var status = supported ? CapabilityStatus.Passed : CapabilityStatus.Unsupported;
        var summary = supported
            ? $"GPU 计算能力 {capability.ToString("0.0", CultureInfo.InvariantCulture)} 在 HALCON 24.11 公布范围内；仍须验证驱动、许可与每个训练任务。"
            : $"GPU 计算能力 {capability.ToString("0.0", CultureInfo.InvariantCulture)} 超出 HALCON 24.11 公布的 5.0–9.0 范围。";
        return new EnvironmentCheck("HALCON GPU support", status, summary,
            "该检查依据公开支持范围作风险分级，不执行 GPU 训练，也不能证明 GPU 运行成功。");
    }

    private static (IReadOnlyList<string> Names, string? Error) QueryDlDevices()
    {
        try
        {
            // The probe worker is a one-job process; its GPU handles are reclaimed by process exit.
            var devices = HDlDevice.QueryAvailableDlDevices("runtime", "gpu");
            var names = new List<string>(devices.Length);
            foreach (var device in devices)
            {
                var name = device.GetDlDeviceParam("name");
                var type = device.GetDlDeviceParam("type");
                names.Add($"{name.S} ({type.S})");
            }

            return (names, null);
        }
        catch (Exception exception)
        {
            return ([], $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static EnvironmentCheck DlDeviceCheck((IReadOnlyList<string> Names, string? Error) result)
    {
        if (result.Names.Count > 0)
            return new EnvironmentCheck("HALCON DL GPU 设备", CapabilityStatus.Passed,
                $"HALCON 枚举到 {result.Names.Count} 个深度学习 GPU 设备。", string.Join(Environment.NewLine, result.Names));
        if (result.Error is not null)
            return new EnvironmentCheck("HALCON DL GPU 设备", CapabilityStatus.Unsupported,
                "HALCON 未能打开深度学习 GPU 设备。", result.Error);
        return new EnvironmentCheck("HALCON DL GPU 设备", CapabilityStatus.Unknown,
            "HALCON 没有报告可用的深度学习 GPU 设备。");
    }

    private static async Task<GpuInfo?> ReadGpuAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,driver_version,memory.total,memory.free --format=csv,noheader,nounits",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        try
        {
            if (!process.Start()) return null;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0) return null;
            return ParseGpu(stdout);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static GpuInfo? ParseGpu(string output)
    {
        var line = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (line is null) return null;
        var fields = line.Split(',').Select(value => value.Trim()).ToArray();
        if (fields.Length < 4) return null;

        var totalBytes = ParseMebibytes(fields[2]);
        var freeBytes = ParseMebibytes(fields[3]);
        return new GpuInfo(fields[0], fields[1], totalBytes, freeBytes, ResolveComputeCapability(fields[0]));
    }

    private static double? ResolveComputeCapability(string name)
    {
        if (name.Contains("RTX 5060", StringComparison.OrdinalIgnoreCase)) return 12.0;
        if (name.Contains("RTX 50", StringComparison.OrdinalIgnoreCase)) return 12.0;
        if (name.Contains("RTX 40", StringComparison.OrdinalIgnoreCase)) return 8.9;
        if (name.Contains("RTX 30", StringComparison.OrdinalIgnoreCase)) return 8.6;
        if (name.Contains("RTX 20", StringComparison.OrdinalIgnoreCase)) return 7.5;
        return null;
    }

    private static long? ParseMebibytes(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mebibytes)
            ? mebibytes * 1024L * 1024L
            : null;

    private static string FormatBytes(long? bytes) => bytes is null
        ? "未知"
        : $"{bytes.Value / 1024d / 1024d:N0} MiB";

    private static string? ReadVersion(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var path = Path.Combine(root, "version.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private sealed record GpuInfo(string Name, string Driver, long? MemoryBytes, long? FreeMemoryBytes, double? ComputeCapability);
}
