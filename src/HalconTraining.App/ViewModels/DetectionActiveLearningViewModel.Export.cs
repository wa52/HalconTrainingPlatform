using System.IO;
using HalconTraining.App.Services;

namespace HalconTraining.App.ViewModels;

public sealed partial class DetectionActiveLearningViewModel
{
    public AsyncCommand ExportHalconCommand { get; }
    public AsyncCommand ExportAllHalconCommand { get; }
    public bool CanExportHalcon => !IsBusy && TrainingSession?.Images.Count>0;
    public bool CanExportAllHalcon => !IsBusy && _session?.Images.Count>0;

    private void RestoreCombinedTrainingFeedback()
    {
        if (_session?.CombinedModelPath is not { } model || !File.Exists(model)) return;
        try
        {
            using var manifest=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(model)!,"manifest.json")));
            var root=manifest.RootElement;
            var epochs=root.GetProperty("epochs").GetInt32();
            var classes=root.GetProperty("classNames").GetArrayLength();
            TrainingEngine="HALCON · 检测";
            BootstrapEpochs=epochs;
            BootstrapBatch=root.GetProperty("batchSize").GetInt32();
            BootstrapSize=root.GetProperty("width").GetInt32();
            BootstrapRate=root.GetProperty("learningRate").GetDouble();
            var losses=root.GetProperty("lossHistory").EnumerateArray().Select(v=>v.GetDouble()).ToArray();
            HasOperationFeedback=true;
            OperationHeadline=$"合并训练完成 · {epochs} Epoch";
            OperationDetail=$"{classes} 类 · 最终训练 Loss {losses.LastOrDefault():F5} · 尚未独立精度评估";
            OperationProgress=100;
            Status="已恢复合并目标检测模型："+model;
            var dataset=root.GetProperty("sourceDataset").GetString();
            if(dataset is not null)LastExport=Path.GetDirectoryName(dataset)!;
        }
        catch(Exception error) when(error is IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        { Status="合并模型仍在，但训练记录无法读取："+error.Message; }
    }

    private async Task<string> ExportHalconSnapshotAsync(DetectionReviewSession scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var folder = await Task.Run(() => _store.ExportTrainingCocoAsync(scope,PredictionMaxBoxes), token);
        var name = string.Concat((string.IsNullOrWhiteSpace(scope.DatasetName) ? "detection" : scope.DatasetName)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = Path.Combine(folder, name + ".hdict");
        var result = await _runner.RunAsync(["--detection-export-hdict",Path.Combine(folder,"annotations.json"),"--output",file],
            new Progress<string>(ReceiveProgress),token);
        await File.WriteAllTextAsync(Path.Combine(folder,"hdict-export.log"),result.Output+Environment.NewLine+result.Error);
        if (result.ExitCode != 0) throw new InvalidOperationException("HALCON 数据集导出失败："+result.Error);
        if (!File.Exists(file)) throw new FileNotFoundException("未生成 HALCON 数据集。",file);
        LastExport = folder;
        return file;
    }

    public Task ExportHalconDatasetAsync(bool allCategories = false) => ExecuteAsync(async () =>
    {
        if (_session is null) return;
        await SaveCurrentTrainingEditsAsync();
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<DetectionReviewSession>(System.Text.Json.JsonSerializer.Serialize(_session))!;
        var scopes = allCategories
            ? new[] { DetectionActiveLearningStore.AllTrainingScope(snapshot,true,PredictionMaxBoxes) }
            : new[] { TrainingSession ?? throw new InvalidOperationException("请选择类别文件夹。") };
        if (scopes.Length == 0) throw new InvalidOperationException("没有可导出的图像。");
        using var cts = new CancellationTokenSource(); _operation=cts;
        OnPropertyChanged(nameof(CanStopOperation));
        StartOperation("导出",scopes.Length);
        try
        {
            var files = new List<string>();
            foreach (var scope in scopes)
            {
                Status = "正在导出并复读校验："+scope.DatasetName;
                files.Add(await ExportHalconSnapshotAsync(scope,cts.Token));
            }
            FinishOperation("HALCON 数据集已导出",$"共 {files.Count} 份；全部未移除图像（含负样本），按类别自动划分 70% / 15% / 15%。",true);
            Status = string.Join(Environment.NewLine,files);
        }
        catch (Exception error) { FinishOperation(cts.IsCancellationRequested?"导出已停止":"导出失败",error.Message,false); throw; }
        finally { _operation=null; OnPropertyChanged(nameof(CanStopOperation)); }
    });
}
