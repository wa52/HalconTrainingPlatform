using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using HalconTraining.Application.Environment;
using HalconTraining.Application.Workers;

namespace HalconTraining.App.Services;

public sealed class NamedPipeTrainingWorkerClient : ITrainingWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<TrainingEnvironmentReport> CaptureEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        var pipeName = $"halcon-training-{Guid.NewGuid():N}";
        var jobId = Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var workerPath = Path.Combine(AppContext.BaseDirectory, "worker", "HalconTraining.Worker.exe");
        if (!File.Exists(workerPath))
            throw new FileNotFoundException("没有找到训练 Worker。请重新生成或发布包含 Worker 的程序目录。", workerPath);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            }
        };
        process.StartInfo.ArgumentList.Add("--environment-probe");
        process.StartInfo.ArgumentList.Add("--pipe");
        process.StartInfo.ArgumentList.Add(pipeName);
        process.StartInfo.ArgumentList.Add("--job-id");
        process.StartInfo.ArgumentList.Add(jobId);

        if (!process.Start()) throw new InvalidOperationException("无法启动 HALCON 训练 Worker。");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null) throw new InvalidDataException("训练 Worker 在发送结果前关闭了通信管道。");

            var message = JsonSerializer.Deserialize<WorkerMessage>(line, JsonOptions)
                ?? throw new InvalidDataException("训练 Worker 返回了空消息。");
            if (message.SchemaVersion != 1 || message.JobId != jobId)
                throw new InvalidDataException("训练 Worker 返回的协议版本或作业编号不匹配。");

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (message.Kind == "WorkerFailed")
            {
                var failure = message.Payload.Deserialize<WorkerFailure>(JsonOptions);
                throw new InvalidOperationException(failure?.Message ?? "训练 Worker 执行失败。");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"训练 Worker 异常退出，退出码 {process.ExitCode}。");
            if (message.Kind != "EnvironmentProbeCompleted")
                throw new InvalidDataException($"训练 Worker 返回了未知消息：{message.Kind}。");

            return message.Payload.Deserialize<TrainingEnvironmentReport>(JsonOptions)
                ?? throw new InvalidDataException("训练 Worker 环境报告格式无效。");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
