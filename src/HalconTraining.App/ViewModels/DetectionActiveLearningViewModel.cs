using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text.Json;
using HalconTraining.Application.Workspace;
using System.Windows;
using Microsoft.Win32;
using HalconTraining.App.Services;

namespace HalconTraining.App.ViewModels;

public sealed class DetectionBoxEditor : ObservableModel
{
    private int _classId;
    private double _confidence, _col1, _row1, _col2, _row2;
    public DetectionBoxEditor(DetectionBox box)
    {
        _classId = box.ClassId; _confidence = box.Confidence;
        _col1 = box.Col1; _row1 = box.Row1; _col2 = box.Col2; _row2 = box.Row2;
    }
    public int ClassId { get => _classId; set => SetProperty(ref _classId, value); }
    public double Confidence => _confidence; // Model score, not an editable ground-truth attribute.
    public double Col1 { get => _col1; set => SetProperty(ref _col1, value); }
    public double Row1 { get => _row1; set => SetProperty(ref _row1, value); }
    public double Col2 { get => _col2; set => SetProperty(ref _col2, value); }
    public double Row2 { get => _row2; set => SetProperty(ref _row2, value); }
    public DetectionBox ToBox() => new(ClassId, Confidence, Col1, Row1, Col2, Row2);
}

public sealed class DetectionReviewRow(DetectionReviewImage image)
{
    public DetectionReviewImage Image { get; } = image;
    public string Name => Path.GetFileName(Image.ImagePath);
    public string Status => Image.Status switch { "approved" => "已确认", "rework" => "需重标", _ => "待审核" };
    public string Priority => Image.Priority.ToString("P0");
    public string Reason => Image.Reason;
    public int BoxCount => Image.PredictedBoxes.Count;
}

public sealed class DetectionActiveLearningViewModel : ObservableModel
{
    private readonly DetectionActiveLearningStore _store;
    private readonly string _workspaceRoot;
    private readonly IHalconProcessRunner _runner;
    private readonly IHalconProcessRunner _yoloRunner;
    private readonly Func<bool> _trainingBusy;
    private CancellationTokenSource? _operation;
    private string? _trainedModel;
    private int _bootstrapEpochs = 40, _bootstrapBatch = 2, _bootstrapSize = 640;
    private double _bootstrapRate = .001;
    private string _trainingEngine = "YOLO · 预训练";
    private DetectionReviewSession? _session;
    private DetectionReviewRow? _selectedRow;
    private DetectionBoxEditor? _selectedBox;
    private bool _busy;
    private string _status = "导入目标检测预测JSON后，按不确定性审核检测框。";
    private string _lastExport = "";
    private IReadOnlyList<DetectionBox> _previewBoxes = [];

    public DetectionActiveLearningViewModel(string workspaceRoot, IHalconProcessRunner runner, IHalconProcessRunner yoloRunner, Func<bool> trainingBusy)
    {
        _workspaceRoot = workspaceRoot;
        _store = new DetectionActiveLearningStore(workspaceRoot);
        _runner = runner;
        _yoloRunner = yoloRunner;
        _trainingBusy = trainingBusy;
        ImportCommand = new AsyncCommand(_ => ExecuteAsync(ImportAsync));
        SeedCommand = new AsyncCommand(_ => ExecuteAsync(CreateSeedAsync));
        AddImagesCommand = new AsyncCommand(_ => ExecuteAsync(AddImagesAsync));
        BootstrapCommand = new AsyncCommand(_ => ExecuteAsync(BootstrapAsync));
        PredictExistingCommand = new AsyncCommand(_ => ExecuteAsync(PredictExistingAsync));
        PredictTrainedCommand = new AsyncCommand(_ => ExecuteAsync(PredictTrainedAsync));
        StopOperationCommand = new RelayCommand(_ => _operation?.Cancel());
        ApproveCommand = new AsyncCommand(_ => ExecuteAsync(() => SaveReviewAsync("approved")));
        ReworkCommand = new AsyncCommand(_ => ExecuteAsync(() => SaveReviewAsync("rework")));
        SaveDraftCommand = new AsyncCommand(_ => ExecuteAsync(() => SaveReviewAsync("pending")));
        ExportCommand = new AsyncCommand(_ => ExecuteAsync(ExportAsync));
        AddBoxCommand = new RelayCommand(_ => AddBox());
        DrawBoxCommand = new RelayCommand(value => DrawBox(value));
        RemoveBoxCommand = new RelayCommand(_ => RemoveBox());
        NextCommand = new RelayCommand(_ => SelectNext());
        OpenExportCommand = new RelayCommand(_ =>
        {
            if (Directory.Exists(LastExport))
                Process.Start(new ProcessStartInfo(LastExport) { UseShellExecute = true });
        });
    }

    public AsyncCommand ImportCommand { get; }
    public AsyncCommand SeedCommand { get; }
    public AsyncCommand AddImagesCommand { get; }
    public AsyncCommand BootstrapCommand { get; }
    public AsyncCommand PredictExistingCommand { get; }
    public AsyncCommand PredictTrainedCommand { get; }
    public RelayCommand StopOperationCommand { get; }
    public AsyncCommand ApproveCommand { get; }
    public AsyncCommand ReworkCommand { get; }
    public AsyncCommand SaveDraftCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public RelayCommand AddBoxCommand { get; }
    public RelayCommand DrawBoxCommand { get; }
    public RelayCommand RemoveBoxCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand OpenExportCommand { get; }

    public ObservableCollection<DetectionReviewRow> Items { get; } = [];
    public ObservableCollection<DetectionBoxEditor> Boxes { get; } = [];
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string LastExport
    {
        get => _lastExport;
        private set { if (SetProperty(ref _lastExport, value)) OnPropertyChanged(nameof(CanOpenExport)); }
    }
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(CanReview));
            OnPropertyChanged(nameof(CanRemoveBox));
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(CanBootstrap));
            OnPropertyChanged(nameof(TrainingEligibility));
            OnPropertyChanged(nameof(CanPredictTrained));
            OnPropertyChanged(nameof(CanAddImages));
            OnPropertyChanged(nameof(CanStopOperation));
        }
    }
    public bool IsIdle => !IsBusy;
    public bool CanReview => !IsBusy && SelectedRow is not null;
    public bool CanAddImages => !IsBusy && _session is not null;
    public bool CanBootstrap => !IsBusy && _session is not null &&
        DetectionActiveLearningStore.ReadyForBootstrap(_session);
    public string TrainingEligibility
    {
        get
        {
            if (_session is null) return "请先创建标注会话";
            var confirmed = _session.Images.Count(image => image.Status == "approved");
            if (confirmed < 5) return $"已确认 {confirmed} 张，还差 {5 - confirmed} 张可启动微调";
            return DetectionActiveLearningStore.ReadyForBootstrap(_session)
                ? $"已确认 {confirmed} 张，可启动预训练模型微调（探索性）"
                : $"已确认 {confirmed} 张，但仍需为每个类别确认至少一个检测框";
        }
    }
    public bool CanPredictTrained => !IsBusy && _trainedModel is not null && File.Exists(_trainedModel);
    public bool CanStopOperation => _operation is not null;
    public string TrainedModel => _trainedModel is null ? "尚无初始检测模型" : Path.GetFileName(Path.GetDirectoryName(_trainedModel));
    public string TrainingEngine
    {
        get => _trainingEngine;
        set
        {
            if (value != "YOLO · 预训练" && value != "HALCON · 检测") return;
            if (!SetProperty(ref _trainingEngine, value)) return;
            BootstrapEpochs = value == "YOLO · 预训练" ? 40 : 5;
            BootstrapBatch = value == "YOLO · 预训练" ? 2 : 1;
            BootstrapSize = value == "YOLO · 预训练" ? 640 : 512;
            BootstrapRate = value == "YOLO · 预训练" ? .001 : .0001;
        }
    }
    public int BootstrapEpochs { get => _bootstrapEpochs; set => SetProperty(ref _bootstrapEpochs, value); }
    public int BootstrapBatch { get => _bootstrapBatch; set => SetProperty(ref _bootstrapBatch, value); }
    public int BootstrapSize { get => _bootstrapSize; set => SetProperty(ref _bootstrapSize, value); }
    public double BootstrapRate { get => _bootstrapRate; set => SetProperty(ref _bootstrapRate, value); }
    public bool CanRemoveBox => CanReview && SelectedBox is not null;
    public bool CanExport => !IsBusy && _session?.Images.Any(image => image.Status == "approved") == true;
    public bool CanOpenExport => Directory.Exists(LastExport);
    public bool HasSession => _session is not null;
    public string ModelInfo => _session is null ? "尚未导入预测" : $"模型：{_session.ModelId} · {_session.Classes.Count} 类";
    public string ClassesInfo => _session is null ? "" : string.Join("    ", _session.Classes.Select(c => $"{c.Id} = {c.Name}"));
    public string ReviewProgress => _session is null ? "0 / 0" :
        $"{_session.Images.Count(i => i.Status == "approved")} 已确认 / {_session.Images.Count(i => i.Status == "pending")} 待审核 / {_session.Images.Count(i => i.Status == "rework")} 需重标";
    public DetectionReviewRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            Boxes.Clear();
            SelectedBox = null;
            if (value is not null)
            {
                var source = value.Image.HasDraft ? value.Image.ReviewedBoxes : value.Image.PredictedBoxes;
                foreach (var box in source)
                {
                    var editor = new DetectionBoxEditor(box);
                    editor.PropertyChanged += (_, _) => UpdatePreview();
                    Boxes.Add(editor);
                }
            }
            UpdatePreview();
            OnPropertyChanged(nameof(PreviewPath));
            OnPropertyChanged(nameof(SelectedDetails));
            OnPropertyChanged(nameof(CanReview));
            OnPropertyChanged(nameof(CanRemoveBox));
        }
    }
    public DetectionBoxEditor? SelectedBox
    {
        get => _selectedBox;
        set { if (SetProperty(ref _selectedBox, value)) OnPropertyChanged(nameof(CanRemoveBox)); }
    }
    public string? PreviewPath => SelectedRow?.Image.ImagePath;
    public IReadOnlyList<DetectionBox> PreviewBoxes { get => _previewBoxes; private set => SetProperty(ref _previewBoxes, value); }
    public string SelectedDetails => SelectedRow is null ? "选择待审核图片" :
        $"{SelectedRow.Image.Width} × {SelectedRow.Image.Height} · {SelectedRow.Status} · {SelectedRow.Reason}";

    public async Task InitializeAsync()
    {
        try
        {
            _session = await _store.LoadLatestAsync();
            _trainedModel = _session?.LastTrainedModelPath;
            OnPropertyChanged(nameof(TrainedModel));
            OnPropertyChanged(nameof(CanPredictTrained));
            if (_session is not null)
            {
                RefreshQueue();
                Status = "已恢复上次目标检测审核会话。";
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        { Status = "恢复审核记录失败：" + error.Message; }
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (Exception error) { Status = "操作失败：" + error.Message; }
        finally { IsBusy = false; }
    }


    private async Task CreateSeedAsync()
    {
        if (_trainingBusy()) throw new InvalidOperationException("请先结束正在进行的训练或测试。");
        var images = DetectionSeedDialogs.SelectImages("选择少量图片，开始目标检测手工标注（支持多选）");
        if (images.Count == 0) return;
        var classes = DetectionSeedDialogs.EnterClasses();
        if (classes is null) return;
        _session = await _store.CreateSeedAsync(images, classes);
        _trainedModel = null;
        LastExport = "";
        OnPropertyChanged(nameof(TrainedModel));
        OnPropertyChanged(nameof(CanPredictTrained));
        RefreshQueue();
        Status = $"已创建初始标注：{images.Count} 张。标多少由你决定；人工确认至少 5 张且每类有目标框，即可微调预训练模型。";
    }

    private async Task AddImagesAsync()
    {
        if (_session is null) throw new InvalidOperationException("请先创建初始标注会话。");
        var images = DetectionSeedDialogs.SelectImages("继续选择原图，保留已有人工确认的检测框");
        if (images.Count == 0) return;
        var added = await _store.AppendSeedImagesAsync(_session, images);
        RefreshQueue();
        Status = $"新增 {added} 张原图；重复图片已跳过，之前的审核与标注不会被清除。";
    }

    private async Task BootstrapAsync()
    {
        if (_session is null || !DetectionActiveLearningStore.ReadyForBootstrap(_session))
            throw new InvalidOperationException("请先人工确认至少 5 张图片，并保证每类至少有一个目标框。");
        if (_trainingBusy()) throw new InvalidOperationException("请先结束主训练任务。");
        if (BootstrapEpochs < 1 || BootstrapBatch < 1 || BootstrapSize < 64 ||
            BootstrapSize % 32 != 0 || !double.IsFinite(BootstrapRate) || BootstrapRate <= 0)
            throw new InvalidOperationException("训练参数无效：尺寸≥64且为32的倍数，Batch/Epoch/学习率需大于0。");
        var yolo = TrainingEngine == "YOLO · 预训练";
        var backbone = Path.Combine(_workspaceRoot, "assets", "pretrained", "pretrained_dl_classifier_compact.hdl");
        if (!yolo && !File.Exists(backbone))
            throw new FileNotFoundException("找不到 HALCON compact 检测骨干基础模型。", backbone);
        var snapshot = await _store.ExportApprovedCocoAsync(_session!);
        var output = Path.Combine(_workspaceRoot, "models", "detection", _session!.Id,
            "run_" + DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(output);
        using var cts = new CancellationTokenSource();
        _operation = cts;
        OnPropertyChanged(nameof(CanStopOperation));
        try
        {
            Status = "开始小样本检测训练 · 模型仅用于自动预标注，尚未精度验收。";
            var progress = new Progress<string>(line =>
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var item = json.RootElement;
                    var stage = item.TryGetProperty("stage", out var value) ? value.GetString() : null;
                    if (stage is "训练" or "轮次完成")
                    {
                        var detail = item.TryGetProperty("detail", out var nested) ? nested : item;
                        if (detail.TryGetProperty("epoch", out var epoch))
                            Status = $"检测训练 · {epoch.GetInt32()}/{BootstrapEpochs} Epoch · {stage}";
                    }
                }
                catch (JsonException) { /* Worker also returns final JSON lines. */ }
            });
            IReadOnlyList<string> arguments = yolo
                ? ["train", "--coco", Path.Combine(snapshot, "annotations.json"),
                   "--output", output, "--model", "yolo26n.pt",
                   "--epochs", BootstrapEpochs.ToString(CultureInfo.InvariantCulture),
                   "--batch", BootstrapBatch.ToString(CultureInfo.InvariantCulture),
                   "--imgsz", BootstrapSize.ToString(CultureInfo.InvariantCulture),
                   "--lr", BootstrapRate.ToString(CultureInfo.InvariantCulture)]
                : ["--detection-bootstrap", Path.Combine(snapshot, "annotations.json"),
                   "--backbone-file", backbone, "--output-dir", output,
                   "--epochs", BootstrapEpochs.ToString(CultureInfo.InvariantCulture),
                   "--batch", BootstrapBatch.ToString(CultureInfo.InvariantCulture),
                   "--width", BootstrapSize.ToString(CultureInfo.InvariantCulture),
                   "--height", BootstrapSize.ToString(CultureInfo.InvariantCulture),
                   "--rate", BootstrapRate.ToString(CultureInfo.InvariantCulture)];
            var result = await (yolo ? _yoloRunner : _runner).RunAsync(arguments, progress, cts.Token);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            _trainedModel = Path.Combine(output, yolo ? "model.pt" : "model.hdl");
            if (!File.Exists(_trainedModel)) throw new FileNotFoundException("训练进程未产生模型文件。", _trainedModel);
            _session.LastTrainedModelPath = _trainedModel;
            await _store.SaveAsync(_session);
            OnPropertyChanged(nameof(TrainedModel));
            OnPropertyChanged(nameof(CanPredictTrained));
            Status = "预训练模型微调完成（未精度验收）。可使用刚训练的模型批量预测，结果仍需逐张审核。";
        }
        catch (OperationCanceledException)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "job.json"),
                JsonSerializer.Serialize(new { status = "interrupted", message = "人工停止训练，模型未发布。" }));
            Status = "初始训练已停止，未产生可用的新模型。";
        }
        catch
        {
            await File.WriteAllTextAsync(Path.Combine(output, "job.json"),
                JsonSerializer.Serialize(new { status = "failed" }));
            throw;
        }
        finally
        {
            _operation = null;
            OnPropertyChanged(nameof(CanStopOperation));
        }
    }

    private async Task PredictExistingAsync()
    {
        if (_trainingBusy()) throw new InvalidOperationException("请先结束当前主训练任务。");
        var model = DetectionSeedDialogs.SelectDetector();
        if (model is not null) await PredictAsync(model);
    }

    private async Task PredictTrainedAsync()
    {
        if (_trainedModel is null || !File.Exists(_trainedModel))
            throw new InvalidOperationException("请先完成初始训练或选择已有检测模型。");
        if (_trainingBusy()) throw new InvalidOperationException("请先结束当前主训练任务。");
        await PredictAsync(_trainedModel);
    }

    private async Task PredictAsync(string modelFile)
    {
        var images = DetectionSeedDialogs.SelectImages("选择未标注图片进行 HALCON 批量预测");
        if (images.Count == 0) return;
        var folder = Path.Combine(_workspaceRoot, "active-learning", "predictions",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var list = Path.Combine(folder, "images.json");
        var resultPath = Path.Combine(folder, "predictions.json");
        await File.WriteAllTextAsync(list, JsonSerializer.Serialize(images.Select(image => image.Path).ToArray()));
        var previous = _session;
        using var cts = new CancellationTokenSource();
        _operation = cts;
        OnPropertyChanged(nameof(CanStopOperation));
        try
        {
            Status = $"正在预测 {images.Count} 张图片；保留低置信度预测供人工复核。";
            var progress = new Progress<string>(line =>
            {
                try
                {
                    using var report = JsonDocument.Parse(line);
                    var info = report.RootElement;
                    if (info.TryGetProperty("stage", out var stage) && stage.GetString() == "预测")
                        Status = $"正在预测 {info.GetProperty("processed").GetInt32()} / {images.Count}";
                }
                catch (JsonException) { }
            });
            var isYolo = Path.GetExtension(modelFile).Equals(".pt", StringComparison.OrdinalIgnoreCase);
            if (!isYolo && !Path.GetExtension(modelFile).Equals(".hdl", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("只支持 YOLO .pt 或 HALCON .hdl 目标检测模型。");
            IReadOnlyList<string> arguments = isYolo
                ? ["predict", "--model", modelFile, "--images-json", list,
                   "--output-json", resultPath, "--imgsz", BootstrapSize.ToString(CultureInfo.InvariantCulture)]
                : ["--detection-predict", modelFile, "--image-list", list, "--output-json", resultPath];
            var result = await (isYolo ? _yoloRunner : _runner).RunAsync(arguments, progress, cts.Token);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            var next = await _store.ImportAsync(resultPath);
            // Preserve verified labels only when class IDs and names are exactly the same.
            var compatible = previous is not null && previous.Classes.Count == next.Classes.Count &&
                previous.Classes.All(category => next.Classes.Any(other =>
                    other.Id == category.Id && other.Name == category.Name));
            if (compatible)
            {
                var existing = next.Images.ToDictionary(i => i.ImagePath, StringComparer.OrdinalIgnoreCase);
                foreach (var accepted in previous!.Images.Where(i => i.Status == "approved"))
                {
                    if (existing.ContainsKey(accepted.ImagePath))
                        next.Images.Remove(existing[accepted.ImagePath]);
                    next.Images.Add(accepted);
                }
            }
            next.LastTrainedModelPath = _trainedModel;
            await _store.SaveAsync(next);
            _session = next;
            RefreshQueue();
            var errorFile = Path.ChangeExtension(resultPath, ".errors.json");
            var failed = File.Exists(errorFile)
                ? JsonDocument.Parse(await File.ReadAllTextAsync(errorFile)).RootElement.GetArrayLength() : 0;
            Status = $"预测完成：{images.Count - failed} 张成功、{failed} 张失败。已加入审核队列。" +
                (compatible ? " 原有已确认标注已保留。" : previous is null ? "" : " 类别定义不同，未合并旧标注。");
        }
        catch (OperationCanceledException) { Status = "批量预测已停止，未覆盖原审核数据。"; }
        finally { _operation = null; OnPropertyChanged(nameof(CanStopOperation)); }
    }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择目标检测预测结果JSON", Filter = "预测JSON|*.json", CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        var session = await _store.ImportAsync(dialog.FileName);
        _session = session;
        LastExport = "";
        RefreshQueue();
        Status = $"导入 {session.Images.Count} 张图片，已按预测不确定性排序；仅人工确认的结果可导出。";
    }

    private void RefreshQueue(string? selectPath = null)
    {
        if (_session is null) return;
        var old = selectPath ?? SelectedRow?.Image.ImagePath;
        var pending = DetectionActiveLearningStore.Pending(_session);
        var others = _session.Images.Where(i => i.Status != "pending")
            .OrderBy(i => i.Status).ThenByDescending(i => i.Priority);
        Items.Clear();
        foreach (var image in pending.Concat(others)) Items.Add(new DetectionReviewRow(image));
        SelectedRow = Items.FirstOrDefault(row => row.Image.ImagePath == old)
            ?? Items.FirstOrDefault(row => row.Image.Status == "pending")
            ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(ReviewProgress));
        OnPropertyChanged(nameof(ModelInfo));
        OnPropertyChanged(nameof(ClassesInfo));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(CanAddImages));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanBootstrap));
        OnPropertyChanged(nameof(TrainingEligibility));
    }

    private void UpdatePreview() => PreviewBoxes = Boxes.Select(box => box.ToBox()).ToArray();

    private void DrawBox(object? parameter)
    {
        if (IsBusy || SelectedRow is null || _session is null || parameter is not DetectionDrawnBox box)
            return;
        var categoryId = SelectedBox?.ClassId ?? _session.Classes[0].Id;
        var editor = new DetectionBoxEditor(new DetectionBox(categoryId, 0,
            box.Col1, box.Row1, box.Col2, box.Row2));
        editor.PropertyChanged += (_, _) => UpdatePreview();
        Boxes.Add(editor);
        SelectedBox = editor;
        UpdatePreview();
        Status = "已在原图绘制检测框；请确认类别ID后点击「接受并下一张」。";
    }

    private void AddBox()
    {
        if (IsBusy || SelectedRow is null || _session is null) return;
        var image = SelectedRow.Image;
        var firstId = _session.Classes[0].Id;
        var box = new DetectionBoxEditor(new DetectionBox(firstId, 0,
            image.Width * .25, image.Height * .25, image.Width * .75, image.Height * .75));
        box.PropertyChanged += (_, _) => UpdatePreview();
        Boxes.Add(box); SelectedBox = box;
        UpdatePreview();
        Status = "已添加手工框；请修改坐标和类别后点击「接受并下一张」。新增框的模型置信度为0，仅作记录。";
    }

    private void RemoveBox()
    {
        if (IsBusy || SelectedBox is null) return;
        Boxes.Remove(SelectedBox); SelectedBox = null; UpdatePreview();
    }

    private void SelectNext()
    {
        if (IsBusy) return;
        SelectedRow = Items.FirstOrDefault(row => row.Image.Status == "pending" && row != SelectedRow)
            ?? Items.FirstOrDefault(row => row != SelectedRow);
    }

    private async Task SaveReviewAsync(string newStatus)
    {
        if (_session is null || SelectedRow is null) return;
        var image = SelectedRow.Image;
        var boxes = Boxes.Select(box => box.ToBox()).ToList();
        // A rework flag or partial draft must be savable even while coordinates are incomplete.
        if (newStatus == "approved") DetectionActiveLearningStore.ValidateReviewedBoxes(image, boxes, _session.Classes);
        image.ReviewedBoxes = boxes;
        image.HasDraft = true;
        image.Status = newStatus;
        await _store.SaveAsync(_session);
        OnPropertyChanged(nameof(CanBootstrap));
        OnPropertyChanged(nameof(TrainingEligibility));
        RefreshQueue(newStatus == "pending" ? image.ImagePath : null);
        if (newStatus != "pending") SelectedRow = Items.FirstOrDefault(row => row.Image.Status == "pending") ?? SelectedRow;
        Status = newStatus switch
        {
            "approved" => "人工确认已保存。该图片会进入COCO导出。",
            "rework" => "已标记需重标，不会导出成训练真值。",
            _ => "草稿已保存，尚未确认，不会导出。"
        };
    }

    public async Task StopAndWaitAsync()
    {
        _operation?.Cancel();
        while (IsBusy) await Task.Delay(50);
    }

    private async Task ExportAsync()
    {
        if (_session is null) return;
        // The export is intentionally based on persisted, human-approved labels only.
        var folder = await Task.Run(() => _store.ExportApprovedCocoAsync(_session));
        LastExport = folder;
        Status = "已导出人工确认图片和COCO标注：" + folder;
    }
}
