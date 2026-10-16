using System.Globalization;
using System.Text.Json;

namespace HalconTraining.Application.Workspace;

public sealed class TrainingWorkspace(IHalconProcessRunner runner, IWorkspaceStore store)
{
    public IWorkspaceStore Store => store;
    private readonly SemaphoreSlim _gpu = new(1, 1);
    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

    public async Task<DatasetReport> InspectAsync(string file, int seed, double train, double validation, CancellationToken token)
    {
        var result = await runner.RunAsync(["--dataset-inspect", file, "--seed", seed.ToString(CultureInfo.InvariantCulture), "--train-percent", Number(train), "--validation-percent", Number(validation)], null, token);
        Ensure(result);
        return JsonSerializer.Deserialize<DatasetReport>(result.Output, WorkspaceJson.Options) ?? throw new InvalidDataException("数据集报告为空。");
    }

    public async Task<string> TrainAsync(TrainingSettings settings, IProgress<TrainingProgress> progress, CancellationToken token)
    {
        if (!await _gpu.WaitAsync(0, token)) throw new InvalidOperationException("已有训练或测试正在执行。");
        string? directory = null;
        try
        {
            if (settings.Task is not ("semantic_segmentation" or "classification")) throw new InvalidOperationException("该任务尚未接入训练。");
            var adapter = settings.Task == "classification" ? "classification" : "semantic";
            directory = store.NewRun(settings.Dataset, settings.Task);
            await store.PrepareAsync(directory, settings);
            await store.SaveStateAsync(directory, "preparing");
            // Progress<T> created on a worker thread invokes callbacks directly, preserving log order.
            var lines = new InlineProgress(line =>
            {
                File.AppendAllText(Path.Combine(directory, "training.log"), line + System.Environment.NewLine);
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement; var detail = root.GetProperty("detail");
                    int Get(string key) => detail.TryGetProperty(key, out var value) ? value.GetInt32() : 0;
                    double? Metric(string key)=>detail.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.Number?value.GetDouble():null;
                    string? Text(string key)=>detail.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
                    progress.Report(new(root.GetProperty("stage").GetString()!, detail.ToString(), Get("epoch"), Get("epochs"), Get("batch"), Get("batches"), Metric("loss"),
                        Metric("meanLoss"),Metric("validationIoU"),Metric("learningRate"),Metric("iterationMs"),Text("previewImage"),Text("previewMask"),Text("truthMask"),Metric("epochSeconds"),Metric("validationAccuracy"),Text("truthLabel"),Text("predictedLabel"),Metric("confidence")));
                }
                catch (JsonException) { progress.Report(new("日志", line)); }
            });
            var result = await runner.RunAsync(["--"+adapter+"-train", settings.Dataset, "--model", settings.ModelFile, "--output", directory,
                "--epochs", settings.Epochs.ToString(), "--batch", settings.Batch.ToString(), "--width", settings.Width.ToString(), "--height", settings.Height.ToString(),
                "--rate", Number(settings.Rate), "--seed", settings.Seed.ToString(), "--train-percent", Number(settings.TrainPercent), "--validation-percent", Number(settings.ValidationPercent),"--validation-interval",settings.ValidationInterval.ToString()], lines, token);
            Ensure(result);
            await store.SaveStateAsync(directory, "validating");
            progress.Report(new("独立评估", "重新加载模型，评估验证集和测试集…"));
            Ensure(await runner.RunAsync(["--"+adapter+"-evaluate", settings.Dataset, "--output", directory], null, token));
            await store.CompleteAsync(directory);
            await store.SaveStateAsync(directory, "completed");
            return directory;
        }
        catch (OperationCanceledException)
        {
            if (directory is not null) await store.SaveStateAsync(directory, "interrupted", "用户停止独立Worker，保留产物；不支持断点续训。");
            throw;
        }
        catch (Exception error)
        {
            if (directory is not null) await store.SaveStateAsync(directory, "failed", error.Message);
            throw;
        }
        finally { _gpu.Release(); }
    }

    public async Task<PredictionResult> TestAsync(string image, ModelEntry model, CancellationToken token)
    {
        if (!model.CanTest) throw new InvalidOperationException("请选择已完成重载与评估的分类或语义分割模型。");
        if (!await _gpu.WaitAsync(0, token)) throw new InvalidOperationException("已有训练或测试正在执行。");
        var directory = store.NewTest(model.Directory);
        try
        {
            await store.ValidateModelAsync(model.Directory);
            var result = await runner.RunAsync([model.Task=="classification"?"--classification-test":"--semantic-test", image, "--model-dir", model.Directory, "--output", directory], null, token);
            Ensure(result);
            return JsonSerializer.Deserialize<PredictionResult>(result.Output, WorkspaceJson.Options) ?? throw new InvalidDataException("预测报告为空。");
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "error.json"), JsonSerializer.Serialize(new { file = image, error = error.Message }, WorkspaceJson.Options), CancellationToken.None);
            throw;
        }
        finally { _gpu.Release(); }
    }
    public async Task SaveOverlayAsync(PredictionResult result, double opacity, CancellationToken token)
    {
        if (result.MaskPath is null) throw new InvalidOperationException("分类结果已保存到result.json，无分割掩膜。");
        Ensure(await runner.RunAsync(["--render-overlay",result.OriginalPath,"--mask",result.MaskPath,"--output",Path.Combine(result.OutputDirectory,"overlay.png"),"--opacity",Number(opacity)],null,token));
    }
    private static void Ensure(WorkerOutput output)
    {
        if (output.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(output.Error) ? $"Worker退出码：{output.ExitCode}" : output.Error.Trim());
    }
    private sealed class InlineProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
}
