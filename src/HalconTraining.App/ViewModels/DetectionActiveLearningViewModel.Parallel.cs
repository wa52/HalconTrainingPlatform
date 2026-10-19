using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using HalconTraining.Application.Workers;
using HalconTraining.App.Services;

namespace HalconTraining.App.ViewModels;

public sealed partial class DetectionActiveLearningViewModel
{
    private readonly SemaphoreSlim _modelRegistration = new(1);
    public ObservableCollection<DetectionParallelJob> ParallelJobs { get; } = [];
    public AsyncCommand ParallelTrainCommand { get; }
    public bool HasParallelJobs => ParallelJobs.Count > 0;
    private IReadOnlyList<DetectionFolderFilter> EligibleParallelCategories => _session is null ? [] :
        CategoryFilters.Where(category => category.Id is { } id && category.Count>=Math.Max(5,BootstrapBatch)&&
            DetectionActiveLearningStore.FolderScope(_session,id).Images.Any(i=>DetectionActiveLearningStore.TrainingBoxes(i,PredictionMaxBoxes).Count>0)).ToArray();
    public bool CanParallelTrain => !IsBusy && IsFolderDataset && !HasParameterErrors && ValidTrainingParameters && EligibleParallelCategories.Count >= 2;
    public Task TrainCategoriesAsync(IReadOnlyList<int> classIds, int concurrency = 2) =>
        ExecuteAsync(() => TrainCategoriesCoreAsync(classIds, concurrency));

    private async Task TrainCategoriesCoreAsync(IReadOnlyList<int> classIds, int concurrency)
    {
        if (_session is null || !IsFolderDataset || HasParameterErrors || !ValidTrainingParameters)
            throw new InvalidOperationException("请先选择文件夹数据集并检查训练参数。");
        if (concurrency is < 1 or > 2 || classIds.Count < 2 || classIds.Distinct().Count() != classIds.Count)
            throw new InvalidOperationException("选择至少两个不同类别，同时运行数为 1 或 2。");
        if (classIds.Any(id => !EligibleParallelCategories.Any(category => category.Id == id)))
            throw new InvalidOperationException("所选类别各自至少需要 5 张图像，Batch 不超过图像数量，不要求已确认。");
        await SaveCurrentTrainingEditsAsync();
        var epochs = BootstrapEpochs; var batch = BootstrapBatch; var size = BootstrapSize; var rate = BootstrapRate;
        var yolo = TrainingEngine == "YOLO · 预训练";
        var usePreviousModel = ContinuePreviousHalconModel;
        var backbone = Path.Combine(_workspaceRoot, "assets", "pretrained", "pretrained_dl_classifier_compact.hdl");
        // A private snapshot per category prevents later UI selection or task completion from changing inputs.
        var source = JsonSerializer.Deserialize<DetectionReviewSession>(JsonSerializer.Serialize(_session))!;
        ParallelJobs.Clear();
        foreach (var id in classIds) ParallelJobs.Add(new DetectionParallelJob(id, source.Classes.Single(c => c.Id == id).Name, epochs));
        OnPropertyChanged(nameof(HasParallelJobs));
        using var cts = new CancellationTokenSource(); _operation = cts;
        OnPropertyChanged(nameof(CanStopOperation));
        StartOperation("训练", ParallelJobs.Count);
        OperationHeadline = $"并行训练 · 最多同时 {concurrency} 类";
        var completed = 0;
        try
        {
            var outcomes = await BoundedJobRunner.RunAsync(ParallelJobs.ToArray(), concurrency, async (job, token) =>
            {
                job.StartedAt = DateTimeOffset.UtcNow; job.State = "准备中";
                var scope = DetectionActiveLearningStore.FolderScope(source, job.ClassId);
                var name = string.Concat(job.ClassName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                job.OutputDirectory = Path.Combine(_workspaceRoot, "models", "detection", name,
                    "run_" + DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(job.OutputDirectory);
                try
                {
                    token.ThrowIfCancellationRequested();
                    scope=DetectionActiveLearningStore.TrainingSnapshot(scope,PredictionMaxBoxes);
                    var datasetFile = yolo ? Path.Combine(await Task.Run(() => _store.ExportTrainingCocoAsync(scope,PredictionMaxBoxes), token),"annotations.json")
                        : await ExportHalconSnapshotAsync(scope,token);
                    token.ThrowIfCancellationRequested();
                    var baseModel = yolo ? CompatibleParallelModel(scope) : backbone;
                    var sourceModel = yolo ? null : HalconDetectionModelSource.Resolve(scope,size,usePreviousModel);
                    if (!yolo && sourceModel!.InitialModelFile is null && !File.Exists(backbone))
                        throw new FileNotFoundException("缺少 HALCON 检测骨干模型。",backbone);
                    IReadOnlyList<string> arguments = yolo
                        ? ["train", "--coco", datasetFile, "--output", job.OutputDirectory,
                            "--model", baseModel, "--epochs", epochs.ToString(CultureInfo.InvariantCulture), "--batch", batch.ToString(CultureInfo.InvariantCulture),
                            "--imgsz", size.ToString(CultureInfo.InvariantCulture), "--lr", rate.ToString(CultureInfo.InvariantCulture)]
                        : ["--detection-train", datasetFile, "--backbone-file", baseModel,
                            "--output-dir", job.OutputDirectory, "--epochs", epochs.ToString(CultureInfo.InvariantCulture), "--batch", batch.ToString(CultureInfo.InvariantCulture),
                            "--width", size.ToString(CultureInfo.InvariantCulture), "--height", size.ToString(CultureInfo.InvariantCulture), "--rate", rate.ToString(CultureInfo.InvariantCulture)];
                    if(sourceModel?.InitialModelFile is { } initialModel) arguments = [..arguments,"--initial-model",initialModel];
                    job.State = "训练中";
                    job.Detail = sourceModel?.Description ?? "独立类别 YOLO 预标注训练";
                    var progress = new Progress<string>(line =>
                    {
                        job.Receive(line);
                        _operationLines.Enqueue($"[{job.ClassName}] {line}"); while (_operationLines.Count > 180) _operationLines.Dequeue();
                        OperationLog = string.Join(Environment.NewLine, _operationLines);
                    });
                    var result = await (yolo ? _yoloRunner : _runner).RunAsync(arguments, progress, token);
                    await File.WriteAllTextAsync(Path.Combine(job.OutputDirectory,"worker.log"), result.Output + Environment.NewLine + result.Error);
                    if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
                    token.ThrowIfCancellationRequested();
                    var model = Path.Combine(job.OutputDirectory, yolo ? "model.pt" : "model.hdl");
                    if (!File.Exists(model)) throw new FileNotFoundException("Worker 未生成模型。",model);
                    await File.WriteAllTextAsync(Path.Combine(job.OutputDirectory,"job.json"),JsonSerializer.Serialize(new { status="completed",classId=job.ClassId,job.ClassName,epochs,batch,imageSize=size,learningRate=rate,modelFile=model }));
                    await _modelRegistration.WaitAsync(token);
                    try
                    {
                        var next = JsonSerializer.Deserialize<DetectionReviewSession>(JsonSerializer.Serialize(_session))!;
                        next.FolderModels[job.ClassId] = model;
                        await _store.SaveAsync(next); _session = next;
                    }
                    finally { _modelRegistration.Release(); }
                    job.State = "已完成"; job.Detail = "独立模型已保存，待预标注和精度验证";
                }
                catch (Exception error)
                {
                    job.State = token.IsCancellationRequested ? "已停止" : "失败";
                    job.Detail = error.Message.Split('\n',StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? error.Message;
                    await File.WriteAllTextAsync(Path.Combine(job.OutputDirectory,"job.json"),JsonSerializer.Serialize(new { status=token.IsCancellationRequested?"interrupted":"failed",classId=job.ClassId,error=error.ToString() }));
                    throw;
                }
                finally
                {
                    job.EndedAt = DateTimeOffset.UtcNow; job.RefreshElapsed(); completed++;
                    _operationCurrent = completed; OnPropertyChanged(nameof(OperationIndeterminate));
                    OperationProgress = completed * 100.0 / ParallelJobs.Count;
                    OperationDetail = $"已结束 {completed}/{ParallelJobs.Count} 类 · 每类模型独立保存";
                }
            }, cts.Token);
            foreach (var outcome in outcomes.Where(outcome => outcome.Cancelled && !outcome.Item.IsFinished))
            { outcome.Item.State = "已停止"; outcome.Item.Detail = "排队任务已取消"; }
            foreach (var outcome in outcomes.Where(outcome => outcome.Error is not null && !outcome.Item.IsFinished))
            { outcome.Item.State = "失败"; outcome.Item.Detail = outcome.Error!.Message; outcome.Item.EndedAt = DateTimeOffset.UtcNow; }
            var succeeded = outcomes.Count(outcome => !outcome.Cancelled && outcome.Error is null);
            var failed = outcomes.Count(outcome => outcome.Error is not null);
            var stopped = outcomes.Count(outcome => outcome.Cancelled);
            var batchFolder = Path.Combine(_workspaceRoot,"active-learning","training-batches"); Directory.CreateDirectory(batchFolder);
            await File.WriteAllTextAsync(Path.Combine(batchFolder,DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..8]+".json"),
                JsonSerializer.Serialize(new { concurrency,epochs,batch,imageSize=size,learningRate=rate,completed=succeeded,failed,stopped,jobs=ParallelJobs.Select(job=>new {job.ClassId,job.ClassName,job.State,job.Detail,job.OutputDirectory,job.StartedAt,job.EndedAt,job.ProcessId}) }));
            RefreshQueue();
            FinishOperation("并行训练已结束", $"完成 {succeeded} 类 · 失败 {failed} 类 · 停止 {stopped} 类", failed == 0 && stopped == 0);
            Status = OperationHeadline + " · " + OperationDetail;
        }
        finally { _operation = null; OnPropertyChanged(nameof(CanStopOperation)); }
    }

    private static string CompatibleParallelModel(DetectionReviewSession scope)
    {
        if (scope.LastTrainedModelPath is not { } model || !File.Exists(model) || Path.GetExtension(model) != ".pt") return "yolo26n.pt";
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(model)!,"manifest.json")));
            var ids = manifest.RootElement.GetProperty("classIds").EnumerateArray().Select(value=>value.GetInt32()).ToArray();
            var names = manifest.RootElement.GetProperty("classNames").EnumerateArray().Select(value=>value.GetString()).ToArray();
            return scope.Classes.Select(category=>category.Id).SequenceEqual(ids) && scope.Classes.Select(category=>category.Name).SequenceEqual(names) ? model : "yolo26n.pt";
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return "yolo26n.pt"; }
    }
}
