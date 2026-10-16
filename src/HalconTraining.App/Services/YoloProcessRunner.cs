using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using HalconTraining.Application.Workspace;

namespace HalconTraining.App.Services;

/// <summary>
/// Runs pretrained YOLO in its own Python process, without loading PyTorch into the WPF/HALCON process.
/// Configure HALCON_TRAINING_PYTHON to select a dedicated Python venv.
/// </summary>
public sealed class YoloProcessRunner : IHalconProcessRunner
{
    public async Task<WorkerOutput> RunAsync(IReadOnlyList<string> arguments, IProgress<string>? output,
        CancellationToken cancellationToken)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "worker", "yolo_active_learning.py");
        if (!File.Exists(script)) throw new FileNotFoundException("找不到 YOLO Python Worker，请重新生成软件。", script);
        var python = Environment.GetEnvironmentVariable("HALCON_TRAINING_PYTHON");
        if (string.IsNullOrWhiteSpace(python)) python = "python";
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 YOLO Python Worker。");
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException(
                "无法找到 Python。请安装 YOLO 运行环境，并设置 HALCON_TRAINING_PYTHON 为 Python.exe 完整路径。", error);
        }
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stderrTask = process.StandardError.ReadToEndAsync();
        var logs = new List<string>();
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            logs.Add(line);
            output?.Report(line);
        }
        await process.WaitForExitAsync(CancellationToken.None);
        var stderr = await stderrTask;
        cancellationToken.ThrowIfCancellationRequested();
        return new WorkerOutput(process.ExitCode, string.Join(Environment.NewLine, logs), stderr);
    }
}
