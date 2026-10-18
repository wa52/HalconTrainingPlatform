using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text.Json;
using HalconTraining.Application.Workspace;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using HalconTraining.App.Services;
using HalconTraining.App.Views;

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

public sealed record DetectionFolderFilter(int? Id, string Name, int Count, int Approved)
{
    public string Caption => $"{Name}  {Approved}/{Count}";
}

public sealed class DetectionReviewRow(DetectionReviewImage image, string? categoryName = null)
{
    public DetectionReviewImage Image { get; } = image;
    public string Name => Path.GetFileName(Image.ImagePath);
    public string ClassName => categoryName ?? "—";
    public string Status => Image.Status switch { "approved" => "已确认", "rework" => "需重标",
        _ when !Image.HasDraft && Image.PredictedBoxes.Count == 0 && Image.Reason.StartsWith("未检测到目标", StringComparison.Ordinal) => "未检出目标",
        _ => "待审核" };
    public string Priority => Image.Priority.ToString("P0");
    public string Reason => Image.Reason;
    public int BoxCount => Image.PredictedBoxes.Count;
}

public sealed partial class DetectionActiveLearningViewModel : ObservableModel
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
    private DetectionFolderFilter? _selectedCategory;
    private IReadOnlyList<DetectionFolderFilter> _categoryFilters = [];
    private int _drawClassId;
    private bool _parameterErrors, _refreshingQueue;
    private bool _predictionParameterErrors;
    private bool _busy;
    private string _status = "导入图片根文件夹，每个子文件夹一个类别；先框选并确认少量图片，再训练和预测其余图像。";
    private string _lastExport = "";
    private IReadOnlyList<DetectionBox> _previewBoxes = [];
    private readonly Stopwatch _operationElapsed = new();
    private readonly DispatcherTimer _operationClock = new() { Interval = TimeSpan.FromSeconds(.5) };
    private readonly Queue<string> _operationLines = new();
    private string _operationKind = "", _operationHeadline = "尚未开始", _operationDetail = "", _operationLog = "";
    private string _trainingScopeName = "";
    private string OperationDatasetName => IsTrainingOperation && _trainingScopeName.Length > 0 ? _trainingScopeName : DatasetName;
    public string CombinedDatasetSummary => _session is null ? "" :
        $"合并训练仅使用已确认图像：{_session.Images.Where(i=>i.Status=="approved").SelectMany(i=>i.ReviewedBoxes.Select(b=>b.ClassId)).Distinct().Count()} 类 · {_session.Images.Count(i=>i.Status=="approved"):N0} 张；其余 {_session.Images.Count(i=>i.Status!="approved"):N0} 张不参与";
    private bool _operationActive, _hasOperationFeedback;
    private double _operationProgress;
    private int _operationTotal, _operationCurrent;
    private string? _restoredModel;
    private bool _thumbnailOverview;

    public DetectionActiveLearningViewModel(string workspaceRoot, IHalconProcessRunner runner, IHalconProcessRunner yoloRunner, Func<bool> trainingBusy)
    {
        _workspaceRoot = workspaceRoot;
        _store = new DetectionActiveLearningStore(workspaceRoot);
        _runner = runner;
        _yoloRunner = yoloRunner;
        _trainingBusy = trainingBusy;
        _operationClock.Tick += (_, _) => { OnPropertyChanged(nameof(OperationElapsedText)); foreach (var job in ParallelJobs) job.RefreshElapsed(); };
        ImportCommand = new AsyncCommand(_ => ExecuteAsync(ImportAsync));
        ImportFolderCommand = new AsyncCommand(_ => ExecuteAsync(async () =>
        {
            var folder = DetectionSeedDialogs.SelectFolder();
            if (folder is not null) await LoadFolderCoreAsync(folder);
        }));
        SeedCommand = new AsyncCommand(_ => ExecuteAsync(CreateSeedAsync));
        AddImagesCommand = new AsyncCommand(_ => ExecuteAsync(AddImagesAsync));
        BootstrapCommand = new AsyncCommand(_ => TrainCurrentCategoryAsync());
        CombinedTrainCommand = new AsyncCommand(_ => TrainCombinedCategoriesAsync());
        ParallelTrainCommand = new AsyncCommand(async _ =>
        {
            if (!CanParallelTrain) { Status = "至少两个类别分别确认 5 张且训练参数有效后可并行训练。"; return; }
            var selection = DetectionParallelTrainingDialog.Select(EligibleParallelCategories, SelectedCategory?.Id,
                BootstrapEpochs, BootstrapBatch, BootstrapSize);
            if (selection is not null) await TrainCategoriesAsync(selection.ClassIds, selection.Concurrency);
        });
        PredictExistingCommand = new AsyncCommand(_ => ExecuteAsync(PredictExistingAsync));
        PredictTrainedCommand = new AsyncCommand(_ => ExecuteAsync(PredictTrainedAsync));
        PredictRemainingCommand = new AsyncCommand(_ => PredictRemainingAsync());
        StopOperationCommand = new RelayCommand(_ => _operation?.Cancel());
        ApproveCommand = new AsyncCommand(_ => ExecuteAsync(() => SaveReviewAsync("approved")));
        ApproveAllCommand = new AsyncCommand(_ => ExecuteAsync(ApproveAllAsync));
        ShowThumbnailOverviewCommand = new RelayCommand(_ => IsThumbnailOverview = true);
        ShowImageEditorCommand = new RelayCommand(_ => IsThumbnailOverview = false);
        OpenThumbnailCommand = new RelayCommand(value =>
        {
            if (IsBusy || value is not DetectionReviewRow row) return;
            SelectedRow = row; IsThumbnailOverview = false;
        });
        ReworkCommand = new AsyncCommand(_ => ExecuteAsync(() => SaveReviewAsync("rework")));
        SaveDraftCommand = new AsyncCommand(_ => ExecuteAsync(() => SaveReviewAsync("pending")));
        ExportCommand = new AsyncCommand(_ => ExecuteAsync(ExportAsync));
        ExportHalconCommand = new AsyncCommand(_ => ExportHalconDatasetAsync());
        ExportAllHalconCommand = new AsyncCommand(_ => ExportHalconDatasetAsync(true));
        AddBoxCommand = new RelayCommand(_ => AddBox());
        DrawBoxCommand = new RelayCommand(value => DrawBox(value));
        EditBoxCommand = new RelayCommand(value => EditBox(value));
        RemoveBoxCommand = new RelayCommand(_ => RemoveBox());
        NextCommand = new RelayCommand(_ => SelectNext());
        OpenExportCommand = new RelayCommand(_ =>
        {
            if (Directory.Exists(LastExport))
                Process.Start(new ProcessStartInfo(LastExport) { UseShellExecute = true });
        });
    }

    public AsyncCommand ImportCommand { get; }
    public AsyncCommand ImportFolderCommand { get; }
    public AsyncCommand SeedCommand { get; }
    public AsyncCommand AddImagesCommand { get; }
    public AsyncCommand BootstrapCommand { get; }
    public AsyncCommand CombinedTrainCommand { get; }
    public bool CanCombinedTrain => !IsBusy && !HasParameterErrors && ValidTrainingParameters && _session?.Images.Count(i=>i.Status=="approved")>=Math.Max(5,BootstrapBatch);
    public AsyncCommand PredictExistingCommand { get; }
    public AsyncCommand PredictTrainedCommand { get; }
    public AsyncCommand PredictRemainingCommand { get; }
    public RelayCommand StopOperationCommand { get; }
    public AsyncCommand ApproveCommand { get; }
    public AsyncCommand ApproveAllCommand { get; }
    public RelayCommand ShowThumbnailOverviewCommand { get; }
    public RelayCommand ShowImageEditorCommand { get; }
    public RelayCommand OpenThumbnailCommand { get; }
    public AsyncCommand ReworkCommand { get; }
    public AsyncCommand SaveDraftCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public RelayCommand AddBoxCommand { get; }
    public RelayCommand DrawBoxCommand { get; }
    public RelayCommand EditBoxCommand { get; }
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
            OnPropertyChanged(nameof(CanExportAllHalcon));
            OnPropertyChanged(nameof(CanBootstrap)); OnPropertyChanged(nameof(CanCombinedTrain));
            OnPropertyChanged(nameof(TrainingEligibility));
            OnPropertyChanged(nameof(CanPredictTrained));
            OnPropertyChanged(nameof(CanAddImages));
            OnPropertyChanged(nameof(CanStopOperation));
            OnPropertyChanged(nameof(CanPredictRemaining));
            OnPropertyChanged(nameof(CanApproveAll));
            OnPropertyChanged(nameof(CanParallelTrain));
        }
    }
    public bool IsIdle => !IsBusy;
    public bool IsThumbnailOverview
    {
        get => _thumbnailOverview;
        set { if (SetProperty(ref _thumbnailOverview, value)) OnPropertyChanged(nameof(IsImageEditor)); }
    }
    public bool IsImageEditor => !IsThumbnailOverview;
    public string ThumbnailSummary => $"{DatasetName} · 全部 {DatasetImageCount:N0} 张 · 点缩略图打开大图";
    public bool IsOperationActive { get => _operationActive; private set { SetProperty(ref _operationActive, value); OnPropertyChanged(nameof(IsTrainingOperation)); OnPropertyChanged(nameof(IsPredictingOperation)); OnPropertyChanged(nameof(TrainButtonLabel)); OnPropertyChanged(nameof(PredictRemainingLabel)); OnPropertyChanged(nameof(TrainingEligibility)); } }
    public bool IsTrainingOperation => IsOperationActive && _operationKind == "训练";
    public bool IsPredictingOperation => IsOperationActive && _operationKind == "预测";
    public string TrainButtonLabel => IsTrainingOperation ? "训练中…" : "本类预标注训练";
    public bool HasOperationFeedback { get => _hasOperationFeedback; private set => SetProperty(ref _hasOperationFeedback, value); }
    public string OperationHeadline { get => _operationHeadline; private set => SetProperty(ref _operationHeadline, value); }
    public string OperationDetail { get => _operationDetail; private set => SetProperty(ref _operationDetail, value); }
    public string OperationLog { get => _operationLog; private set => SetProperty(ref _operationLog, value); }
    public string OperationElapsedText => _operationElapsed.IsRunning || _operationElapsed.Elapsed > TimeSpan.Zero ? $"耗时 {_operationElapsed.Elapsed:mm\\:ss}" : "";
    public double OperationProgress { get => _operationProgress; private set { SetProperty(ref _operationProgress, value); OnPropertyChanged(nameof(OperationIndeterminate)); } }
    public bool OperationIndeterminate => IsOperationActive && _operationCurrent == 0;
    public bool CanReview => !IsBusy && SelectedRow is not null;
    public int BulkApprovalCount => _session is null || IsFolderDataset && SelectedCategory?.Id is null ? 0 :
        DetectionActiveLearningStore.BulkApprovalCandidates(_session, IsFolderDataset ? SelectedCategory?.Id : null, PredictionMaxBoxes).Count;
    public bool CanApproveAll => !IsBusy && BulkApprovalCount > 0 && !HasPredictionParameterErrors;
    public string ApproveAllLabel => $"全部确认（{BulkApprovalCount:N0}）";
    public bool CanAddImages => !IsBusy && _session is not null;
    private bool IsFolderDataset => _session?.SourceDirectory is not null;
    private DetectionReviewSession? TrainingSession => IsFolderDataset
        ? SelectedCategory?.Id is { } id ? DetectionActiveLearningStore.FolderScope(_session!, id) : null
        : _session;
    public bool CanBootstrap => !IsBusy && !HasParameterErrors && ValidTrainingParameters && TrainingSession is { } scope &&
        BootstrapBatch <= scope.Images.Count(image => image.Status == "approved") &&
        DetectionActiveLearningStore.ReadyForBootstrap(scope);
    private bool ValidTrainingParameters => BootstrapEpochs > 0 && BootstrapBatch > 0 && BootstrapSize >= 64 &&
        BootstrapSize % 32 == 0 && double.IsFinite(BootstrapRate) && BootstrapRate > 0 && BootstrapRate < 1;
    public bool HasParameterErrors
    {
        get => _parameterErrors;
        set { if (SetProperty(ref _parameterErrors, value)) NotifyTrainingParameters(); }
    }
    public string TrainingEligibility
    {
        get
        {
            if (_session is null) return "选择根文件夹，自动识别类别后开始画框。";
            if (IsOperationActive) return $"{OperationDatasetName} · {OperationHeadline}";
            if (HasParameterErrors || !ValidTrainingParameters) return "请检查训练参数；尺寸需为 32 的倍数，学习率需在 0～1 之间。";
            var scope = TrainingSession;
            if (scope is null) return "请选择一个类别文件夹；各类别独立训练和预测。";
            var confirmed = scope.Images.Count(image => image.Status == "approved");
            if (confirmed < 5) return $"已确认 {confirmed} 张，还差 {5 - confirmed} 张可启动微调";
            if (BootstrapBatch > confirmed) return "Batch 不能大于已确认的图片数量。";
            var missing = scope.Classes.Where(category => !scope.Images.Where(image => image.Status == "approved")
                .SelectMany(image => image.ReviewedBoxes).Any(box => box.ClassId == category.Id)).Select(category => category.Name).ToArray();
            return DetectionActiveLearningStore.ReadyForBootstrap(scope)
                ? $"{DatasetName} · 已确认 {confirmed} 张，可独立训练此类别（探索性）"
                : "以下类别还需确认检测框：" + string.Join("、", missing);
        }
    }
    public int PredictionMaxBoxes
    {
        get => _session?.PredictionMaxBoxes ?? (IsFolderDataset ? 1 : 300);
        set
        {
            if (value is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(value), "每图最多预测框数需在 1～300 之间。");
            if (_session is null || _session.PredictionMaxBoxes == value) return;
            _session.PredictionMaxBoxes = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(PredictionPolicy));
            OnPropertyChanged(nameof(BulkApprovalCount)); OnPropertyChanged(nameof(ApproveAllLabel)); OnPropertyChanged(nameof(CanApproveAll));
        }
    }
    public double PredictionConfidence
    {
        get => IsFolderDataset && SelectedCategory?.Id is { } id
            ? _session!.FolderPredictionConfidences.GetValueOrDefault(id, .05) : _session?.PredictionConfidence ?? .05;
        set
        {
            if (!double.IsFinite(value) || value <= 0 || value > 1)
                throw new ArgumentOutOfRangeException(nameof(value), "预测阈值需大于 0 且不超过 1。");
            if (_session is null) return;
            if (IsFolderDataset && SelectedCategory?.Id is { } id) _session.FolderPredictionConfidences[id] = value;
            else _session.PredictionConfidence = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(PredictionPolicy));
        }
    }
    public bool HasPredictionParameterErrors
    {
        get => _predictionParameterErrors;
        set
        {
            if (!SetProperty(ref _predictionParameterErrors, value)) return;
            OnPropertyChanged(nameof(CanPredictTrained)); OnPropertyChanged(nameof(CanPredictRemaining));
            OnPropertyChanged(nameof(CanApproveAll));
        }
    }
    public string PredictionPolicy => $"预测阈值 {PredictionConfidence:P1} · 每图最多 {PredictionMaxBoxes} 个预测框 · 仍需人工确认";
    public bool CanPredictTrained => !IsBusy && !HasPredictionParameterErrors && _trainedModel is not null && File.Exists(_trainedModel);
    public bool HasTrainedModel => _trainedModel is not null && File.Exists(_trainedModel);
    public int RemainingPredictionCount => TrainingSession is not { } scope ? 0 : DetectionActiveLearningStore.PredictionCandidates(scope).Count;
    public string PredictRemainingLabel => IsPredictingOperation ? "预测中…" : $"预测其余图像（{RemainingPredictionCount:N0}）";
    public bool CanPredictRemaining => !IsBusy && !HasPredictionParameterErrors && HasTrainedModel && RemainingPredictionCount > 0;
    public bool CanStopOperation => _operation is not null;
    public string TrainedModel => _trainedModel is null ? "尚无初始检测模型" : Path.GetFileName(Path.GetDirectoryName(_trainedModel)) ?? Path.GetFileName(_trainedModel);
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
    public int BootstrapEpochs { get => _bootstrapEpochs; set { SetProperty(ref _bootstrapEpochs, value); NotifyTrainingParameters(); } }
    public int BootstrapBatch { get => _bootstrapBatch; set { SetProperty(ref _bootstrapBatch, value); NotifyTrainingParameters(); } }
    public int BootstrapSize { get => _bootstrapSize; set { SetProperty(ref _bootstrapSize, value); NotifyTrainingParameters(); } }
    public double BootstrapRate { get => _bootstrapRate; set { SetProperty(ref _bootstrapRate, value); NotifyTrainingParameters(); } }
    private void NotifyTrainingParameters() { OnPropertyChanged(nameof(CanBootstrap)); OnPropertyChanged(nameof(CanCombinedTrain)); OnPropertyChanged(nameof(CanParallelTrain)); OnPropertyChanged(nameof(TrainingEligibility)); }
    public bool CanRemoveBox => CanReview && SelectedBox is not null;
    public bool CanExport => !IsBusy && TrainingSession?.Images.Any(image => image.Status == "approved") == true;
    public bool CanOpenExport => Directory.Exists(LastExport);
    public bool HasSession => _session is not null;
    public int RootImageCount => _session?.Images.Count ?? 0;
    public int DatasetImageCount => TrainingSession?.Images.Count ?? 0;
    public string DatasetName => IsFolderDataset ? SelectedCategory?.Name ?? "请选择类别" : string.IsNullOrEmpty(_session?.DatasetName) ? "目标检测" : _session.DatasetName;
    public string? SourceDirectory => TrainingSession?.SourceDirectory;
    public IReadOnlyList<string> ImportIssues => _session?.ImportIssues ?? [];
    public IReadOnlyList<DetectionCategory> Categories => TrainingSession?.Classes ?? [];
    public IReadOnlyList<DetectionFolderFilter> CategoryFilters { get => _categoryFilters; private set => SetProperty(ref _categoryFilters, value); }
    public DetectionFolderFilter? SelectedCategory
    {
        get => _selectedCategory;
        set { if (SetProperty(ref _selectedCategory, value) && !_refreshingQueue) RefreshQueue(); }
    }
    public int DrawClassId
    {
        get => _drawClassId;
        set { if (SetProperty(ref _drawClassId, value) && SelectedBox is not null) SelectedBox.ClassId = value; }
    }
    public string ModelInfo => _session is null ? "每个子文件夹独立标注、训练和预测" : IsFolderDataset
        ? $"{DatasetName} · {DatasetImageCount:N0} 张 · 独立类别（共 {_session.Classes.Count} 个文件夹）"
        : $"{DatasetName} · {_session.Images.Count:N0} 张 · {_session.Classes.Count} 类";
    public string ClassesInfo => _session is null ? "" : string.Join("    ", _session.Classes.Select(c => $"{c.Id} = {c.Name}"));
    public string ReviewProgress => TrainingSession is not { } scope ? "0 / 0" :
        $"{scope.Images.Count(i => i.Status == "approved")} 已确认 / {scope.Images.Count(i => i.Status == "pending")} 待审核 / {scope.Images.Count(i => i.Status == "rework")} 需重标";
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
                var source = DetectionActiveLearningStore.ReviewBoxes(value.Image, PredictionMaxBoxes);
                foreach (var box in source)
                {
                    var editor = new DetectionBoxEditor(box);
                    editor.PropertyChanged += (_, _) => UpdatePreview();
                    Boxes.Add(editor);
                }
                DrawClassId = value.Image.FolderClassId ?? Boxes.FirstOrDefault()?.ClassId ?? Categories.FirstOrDefault()?.Id ?? 0;
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
        set { if (SetProperty(ref _selectedBox, value)) { if (value is not null) DrawClassId = value.ClassId; OnPropertyChanged(nameof(CanRemoveBox)); OnPropertyChanged(nameof(SelectedBoxIndex)); } }
    }
    public int SelectedBoxIndex
    {
        get => SelectedBox is null ? -1 : Boxes.IndexOf(SelectedBox);
        set => SelectedBox = value >= 0 && value < Boxes.Count ? Boxes[value] : null;
    }
    public string? PreviewPath => SelectedRow?.Image.ImagePath;
    public IReadOnlyList<DetectionBox> PreviewBoxes { get => _previewBoxes; private set => SetProperty(ref _previewBoxes, value); }
    public string SelectedDetails => SelectedRow is null ? "选择待审核图片" :
        $"{SelectedRow.ClassName} / {SelectedRow.Name} · {SelectedRow.Image.Width} × {SelectedRow.Image.Height} · {SelectedRow.Status} · {Boxes.Count} 个框";

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
                RestoreCombinedTrainingFeedback();
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        { Status = "恢复审核记录失败：" + error.Message; }
    }

    private void StartOperation(string kind, int total)
    {
        _operationKind = kind; _operationTotal = total; _operationCurrent = 0;
        _operationLines.Clear(); OperationLog = ""; OperationProgress = 0;
        HasOperationFeedback = true; IsOperationActive = true;
        OperationHeadline = "准备" + kind; OperationDetail = $"{OperationDatasetName} · 正在启动，请稍候…";
        _operationElapsed.Restart(); _operationClock.Start(); OnPropertyChanged(nameof(OperationElapsedText));
        OnPropertyChanged(nameof(OperationIndeterminate));
    }

    private void RestoreModelFeedback()
    {
        if (IsOperationActive || string.Equals(_restoredModel, _trainedModel, StringComparison.OrdinalIgnoreCase)) return;
        _restoredModel = _trainedModel;
        _operationElapsed.Reset(); OnPropertyChanged(nameof(OperationElapsedText));
        if (!HasTrainedModel) { HasOperationFeedback = false; return; }
        var epochText = "";
        try
        {
            var manifest = Path.Combine(Path.GetDirectoryName(_trainedModel!)!, "manifest.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            if (root.TryGetProperty("epochs", out var epochs) && epochs.TryGetInt32(out var count))
            { if(_session?.CombinedModelPath is null)BootstrapEpochs = count; epochText = $" · {count} Epoch"; }
            if (_session?.CombinedModelPath is null)
            {
                if (root.TryGetProperty("batch", out var batch) && batch.TryGetInt32(out var size)) BootstrapBatch = size;
                if (root.TryGetProperty("imageSize", out var imageSize) && imageSize.TryGetInt32(out var width)) BootstrapSize = width;
                if (root.TryGetProperty("learningRate", out var rate) && rate.TryGetDouble(out var value)) BootstrapRate = value;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { }
        HasOperationFeedback = true; OperationProgress = 100;
        OperationHeadline = "已恢复预标注模型" + epochText;
        OperationDetail = $"{DatasetName} · 可预测当前类别其余图像。";
        if (TrainingSession is { } restoredScope)
        {
            var pending = restoredScope.Images.Where(image => image.Status == "pending").ToArray();
            if (pending.Length > 0) OperationDetail = $"{DatasetName} · 待审核 {pending.Length:N0} 张 · {pending.Count(image => image.PredictedBoxes.Count > 0):N0} 张有预测框。";
        }
        if (TrainingSession is { } scope && scope.Images.Any(image => image.Status == "pending") &&
            scope.Images.Where(image => image.Status == "pending").All(image => !image.HasDraft &&
                image.PredictedBoxes.Count == 0 && image.Reason.StartsWith("未检测到目标", StringComparison.Ordinal)))
        {
            OperationHeadline = "上次预测 · 未检出目标";
            OperationDetail = $"{DatasetName} · 未检出 {scope.Images.Count(image => image.Status == "pending"):N0} 张；可调整预测阈值，或补充标注后继续训练。";
        }
    }

    private void ReceiveProgress(string line)
    {
        if (!IsOperationActive) return;
        _operationLines.Enqueue(line); while (_operationLines.Count > 180) _operationLines.Dequeue();
        OperationLog = string.Join(Environment.NewLine, _operationLines);
        var message = DetectionProgressMessage.Parse(line);
        if (message is null) return;
        OperationHeadline = message.Stage;
        if (message.Current is { } current)
        {
            _operationCurrent = current;
            if (message.Total is > 0) _operationTotal = message.Total.Value;
            OperationProgress = _operationTotal > 0 ? Math.Clamp(current * 95.0 / _operationTotal, 0, 95) : 0;
            OperationHeadline = _operationKind == "训练" ? $"训练中 · Epoch {current}/{_operationTotal}" : $"预测中 · {current}/{_operationTotal}";
        }
        OperationDetail = $"{OperationDatasetName}" + (message.Loss is { } loss ? $" · Loss {loss:F5}" : "") + " · " + OperationElapsedText;
        Status = OperationHeadline + " · " + OperationDetail;
        OnPropertyChanged(nameof(TrainingEligibility));
    }

    private void FinishOperation(string headline, string detail, bool success)
    {
        _operationElapsed.Stop(); _operationClock.Stop();
        IsOperationActive = false; OperationHeadline = headline; OperationDetail = detail;
        if (success) OperationProgress = 100;
        OnPropertyChanged(nameof(OperationIndeterminate)); OnPropertyChanged(nameof(OperationElapsedText));
        OnPropertyChanged(nameof(TrainingEligibility));
    }

    public Task TrainCurrentCategoryAsync() => ExecuteAsync(BootstrapAsync);

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (IsBusy) return;
        if (_trainingBusy()) { Status = "请先结束正在进行的训练或测试。"; return; }
        IsBusy = true;
        try { await action(); }
        catch (Exception error)
        {
            if (IsOperationActive) FinishOperation(_operationKind + "失败", error.Message, false);
            Status = "操作失败：" + error.Message;
        }
        finally { IsBusy = false; }
    }

    public Task ImportFolderAsync(string folder) => ExecuteAsync(() => LoadFolderCoreAsync(folder));

    private async Task LoadFolderCoreAsync(string folder)
    {
        using var cts = new CancellationTokenSource();
        _operation = cts;
        OnPropertyChanged(nameof(CanStopOperation));
        try
        {
            Status = "正在读取文件夹和图片尺寸…";
            var progress = new Progress<string>(message => Status = message);
            _session = await Task.Run(() => _store.ImportFolderAsync(folder, DetectionSeedDialogs.ReadImageSize, _session, progress, cts.Token));
            _trainedModel = _session.LastTrainedModelPath;
            LastExport = "";
            _selectedCategory = null;
            RefreshQueue();
            Status = $"已读取 {_session.Images.Count:N0} 张、{_session.Classes.Count} 个独立类别。选类别和图片画框，确认后训练。" +
                (_session.ImportIssues.Count > 0 ? $" 读取提示 {_session.ImportIssues.Count} 项，可在更多操作中查看。" : "");
        }
        catch (OperationCanceledException) { Status = "读取已停止，原有会话和源文件保持不变。"; }
        finally { _operation = null; OnPropertyChanged(nameof(CanStopOperation)); }
    }

    public Task PredictRemainingAsync() => ExecuteAsync(async () =>
    {
        if (!HasTrainedModel || _session is null) throw new InvalidOperationException("请先确认少量图片并完成训练。");
        var scope = TrainingSession ?? throw new InvalidOperationException("请选择一个类别文件夹。");
        var images = DetectionActiveLearningStore.PredictionCandidates(scope)
            .Select(image => (Path: image.ImagePath, image.Width, image.Height)).ToArray();
        if (images.Length == 0) { Status = "没有待预测图像；已确认标注和人工草稿均已保留。"; return; }
        await PredictAsync(_trainedModel!, images);
    });


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

    private string ResolveYoloBaseModel()
    {
        // Continue fine-tuning only when the previous YOLO weights have precisely
        // the same class mapping as the current human-reviewed dataset.
        if (_session is null || _trainedModel is null ||
            !Path.GetExtension(_trainedModel).Equals(".pt", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(_trainedModel))
            return "yolo26n.pt";
        var manifest = Path.Combine(Path.GetDirectoryName(_trainedModel)!, "manifest.json");
        if (!File.Exists(manifest)) return "yolo26n.pt";
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = json.RootElement;
            var ids = root.GetProperty("classIds").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var names = root.GetProperty("classNames").EnumerateArray().Select(v => v.GetString()).ToArray();
            if (TrainingSession is { } scope && ids.Length == scope.Classes.Count &&
                scope.Classes.Select((category, index) =>
                    category.Id == ids[index] && category.Name == names[index]).All(match => match))
                return _trainedModel;
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A corrupt or mismatched model never silently overrides the pretrained seed.
        }
        return "yolo26n.pt";
    }

    public Task TrainCombinedCategoriesAsync() => ExecuteAsync(() => TrainDetectionScopeAsync(
        _session is null ? null : DetectionActiveLearningStore.CombinedApprovedScope(_session),true));

    private Task BootstrapAsync() => TrainDetectionScopeAsync(TrainingSession,false);

    private async Task TrainDetectionScopeAsync(DetectionReviewSession? scope,bool combined)
    {
        if (scope is null || !DetectionActiveLearningStore.ReadyForBootstrap(scope))
            throw new InvalidOperationException("请先人工确认至少 5 张图片，并保证每类至少有一个目标框。");
        if (_trainingBusy()) throw new InvalidOperationException("请先结束主训练任务。");
        if (HasParameterErrors || !ValidTrainingParameters || BootstrapBatch > scope.Images.Count(image => image.Status == "approved"))
            throw new InvalidOperationException("训练参数无效：尺寸≥64且为32的倍数，Batch/Epoch/学习率需大于0。");
        var yolo = !combined && TrainingEngine == "YOLO · 预训练";
        var yoloBase = yolo ? ResolveYoloBaseModel() : "";
        var backbone = Path.Combine(_workspaceRoot, "assets", "pretrained", "pretrained_dl_classifier_compact.hdl");
        if (!yolo && !File.Exists(backbone))
            throw new FileNotFoundException("找不到 HALCON compact 检测骨干基础模型。", backbone);
        ParallelJobs.Clear(); OnPropertyChanged(nameof(HasParallelJobs));
        _trainingScopeName = combined ? $"{scope.DatasetName} · 合并 {scope.Classes.Count} 类 · {scope.Images.Count(i=>i.Status=="approved"):N0} 张已确认" : scope.DatasetName;
        StartOperation("训练", BootstrapEpochs);
        var datasetFolder = string.IsNullOrEmpty(scope.DatasetName) ? scope.Id :
            string.Concat(scope.DatasetName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var output = Path.Combine(_workspaceRoot, "models", "detection", datasetFolder,
            "run_" + DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(output);
        using var cts = new CancellationTokenSource();
        _operation = cts;
        OnPropertyChanged(nameof(CanStopOperation));
        try
        {
            var datasetFile = yolo ? Path.Combine(await Task.Run(() => _store.ExportApprovedCocoAsync(scope)), "annotations.json")
                : await ExportHalconSnapshotAsync(scope,cts.Token);
            Status = combined ? $"HALCON 合并训练 · {scope.Classes.Count} 类 · 仅已确认图像，尚未精度验收。"
                : "开始小样本检测训练 · 模型仅用于自动预标注，尚未精度验收。";
            var progress = new Progress<string>(ReceiveProgress);
            IReadOnlyList<string> arguments = yolo
                ? ["train", "--coco", datasetFile,
                   "--output", output, "--model", yoloBase,
                   "--epochs", BootstrapEpochs.ToString(CultureInfo.InvariantCulture),
                   "--batch", BootstrapBatch.ToString(CultureInfo.InvariantCulture),
                   "--imgsz", BootstrapSize.ToString(CultureInfo.InvariantCulture),
                   "--lr", BootstrapRate.ToString(CultureInfo.InvariantCulture)]
                : ["--detection-train", datasetFile,
                   "--backbone-file", backbone, "--output-dir", output,
                   "--epochs", BootstrapEpochs.ToString(CultureInfo.InvariantCulture),
                   "--batch", BootstrapBatch.ToString(CultureInfo.InvariantCulture),
                   "--width", BootstrapSize.ToString(CultureInfo.InvariantCulture),
                   "--height", BootstrapSize.ToString(CultureInfo.InvariantCulture),
                   "--rate", BootstrapRate.ToString(CultureInfo.InvariantCulture)];
            var result = await (yolo ? _yoloRunner : _runner).RunAsync(arguments, progress, cts.Token);
            await File.WriteAllTextAsync(Path.Combine(output, "training.log"), result.Output + Environment.NewLine + result.Error);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            var newModel = Path.Combine(output, yolo ? "model.pt" : "model.hdl");
            if (!File.Exists(newModel)) throw new FileNotFoundException("训练进程未产生模型文件。", newModel);
            if (!combined) _trainedModel = newModel;
            if (combined) _session!.CombinedModelPath = newModel;
            else if (IsFolderDataset && SelectedCategory?.Id is { } categoryId) _session!.FolderModels[categoryId] = newModel;
            else _session!.LastTrainedModelPath = _trainedModel;
            await _store.SaveAsync(_session);
            OnPropertyChanged(nameof(TrainedModel));
            OnPropertyChanged(nameof(CanPredictTrained));
            OnPropertyChanged(nameof(HasTrainedModel));
            OnPropertyChanged(nameof(CanPredictRemaining));
            FinishOperation($"训练完成 · {BootstrapEpochs} Epoch", combined ? $"{scope.Classes.Count} 类合并模型已保存：{newModel}" : $"{DatasetName} · {OperationElapsedText} · 模型已保存，可点击“预测其余图像”。", true);
            Status = OperationHeadline + " · " + OperationDetail;
        }
        catch (OperationCanceledException)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "job.json"),
                JsonSerializer.Serialize(new { status = "interrupted", message = "人工停止训练，模型未发布。" }));
            FinishOperation("训练已停止", "已保留标注和上次模型。", false);
            Status = "初始训练已停止，未产生可用的新模型。";
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "job.json"),
                JsonSerializer.Serialize(new { status = "failed", message = error.Message }));
            FinishOperation("训练失败", error.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? error.Message, false);
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

    private async Task PredictAsync(string modelFile, IReadOnlyList<(string Path, int Width, int Height)>? selectedImages = null)
    {
        if (HasPredictionParameterErrors) throw new InvalidDataException("请检查预测阈值和每图最多预测框数。");
        var maxBoxes = PredictionMaxBoxes;
        var confidence = PredictionConfidence;
        var images = selectedImages ?? DetectionSeedDialogs.SelectImages("选择图片进行批量预测");
        if (images.Count == 0) return;
        if (IsFolderDataset)
        {
            var allowed = TrainingSession?.Images.Select(image => image.ImagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (allowed is null || images.Any(image => !allowed.Contains(image.Path)))
                throw new InvalidDataException("请选择当前类别文件夹中的图片；各文件夹独立预测。");
        }
        var folder = Path.Combine(_workspaceRoot, "active-learning", "predictions",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "prediction-options.json"),
            JsonSerializer.Serialize(new { maxDetections = maxBoxes, confidence }));
        if (_session is not null) await _store.SaveAsync(_session);
        var list = Path.Combine(folder, "images.json");
        var resultPath = Path.Combine(folder, "predictions.json");
        await File.WriteAllTextAsync(list, JsonSerializer.Serialize(images.Select(image => image.Path).ToArray()));
        var previous = _session;
        using var cts = new CancellationTokenSource();
        _operation = cts;
        OnPropertyChanged(nameof(CanStopOperation));
        try
        {
            ParallelJobs.Clear(); OnPropertyChanged(nameof(HasParallelJobs));
            StartOperation("预测", images.Count);
            Status = $"正在预测 {images.Count} 张图片；保留低置信度预测供人工复核。";
            var progress = new Progress<string>(ReceiveProgress);
            var isYolo = Path.GetExtension(modelFile).Equals(".pt", StringComparison.OrdinalIgnoreCase);
            if (!isYolo && !Path.GetExtension(modelFile).Equals(".hdl", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("只支持 YOLO .pt 或 HALCON .hdl 目标检测模型。");
            IReadOnlyList<string> arguments = isYolo
                ? ["predict", "--model", modelFile, "--images-json", list,
                   "--output-json", resultPath, "--imgsz", BootstrapSize.ToString(CultureInfo.InvariantCulture),
                   "--max-det", maxBoxes.ToString(CultureInfo.InvariantCulture),
                   "--confidence", confidence.ToString(CultureInfo.InvariantCulture)]
                : ["--detection-predict", modelFile, "--image-list", list, "--output-json", resultPath,
                   "--confidence", confidence.ToString(CultureInfo.InvariantCulture)];
            var result = await (isYolo ? _yoloRunner : _runner).RunAsync(arguments, progress, cts.Token);
            await File.WriteAllTextAsync(Path.Combine(folder, "prediction.log"), result.Output + Environment.NewLine + result.Error);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
            var proposals = await _store.ImportAsync(resultPath, persist: false);
            foreach (var proposal in proposals.Images)
            {
                proposal.PredictedBoxes = DetectionActiveLearningStore.ReviewBoxes(proposal, maxBoxes)
                    .Where(box => box.Confidence >= confidence).ToList();
                (proposal.Priority, proposal.Reason) = DetectionActiveLearningStore.Rank(proposal.PredictedBoxes);
            }
            proposals.PredictionMaxBoxes = maxBoxes;
            var next = previous is null ? proposals : IsFolderDataset && SelectedCategory?.Id is { } folderId
                ? DetectionActiveLearningStore.MergeFolderPredictions(previous, folderId, proposals)
                : DetectionActiveLearningStore.MergePredictions(previous, proposals);
            if (IsFolderDataset && SelectedCategory?.Id is { } classId) next.FolderModels[classId] = modelFile;
            else next.LastTrainedModelPath = modelFile;
            await _store.SaveAsync(next);
            _trainedModel = modelFile;
            _session = next;
            RefreshQueue();
            var errorFile = Path.ChangeExtension(resultPath, ".errors.json");
            var failed = 0;
            if (File.Exists(errorFile))
            {
                using var errors = JsonDocument.Parse(await File.ReadAllTextAsync(errorFile));
                failed = errors.RootElement.GetArrayLength();
            }
            var summary = DetectionPredictionSummary.From(proposals.Images, failed);
            await File.WriteAllTextAsync(Path.Combine(folder, "prediction-summary.json"), JsonSerializer.Serialize(summary));
            FinishOperation(summary.DetectedImages == 0 ? "预测完成 · 未检出目标" : "预测完成",
                $"{DatasetName} · {summary.Description}" + (summary.DetectedImages == 0
                    ? $" · 阈值 {confidence:P1}；可调整阈值或补充标注后继续训练。" : " · 有框图片待审核。"), true);
            Status = OperationDetail + " 已保留原有人工标注。";
        }
        catch (OperationCanceledException) { FinishOperation("预测已停止", "原有审核数据已保留。", false); Status = "批量预测已停止，未覆盖原审核数据。"; }
        catch (Exception error) { FinishOperation("预测失败", error.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? error.Message, false); throw; }
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
        _trainedModel = session.LastTrainedModelPath;
        _selectedCategory = null;
        LastExport = "";
        RefreshQueue();
        Status = $"导入 {session.Images.Count} 张图片，已按预测不确定性排序；仅人工确认的结果可导出。";
    }

    private void RefreshQueue(string? selectPath = null)
    {
        if (_session is null || _refreshingQueue) return;
        _refreshingQueue = true;
        try
        {
        var old = selectPath ?? SelectedRow?.Image.ImagePath;
        var selectedId = SelectedCategory?.Id ?? _session.SelectedFolderClassId ??
            (_session.FolderModels.Count > 0 ? _session.FolderModels.Keys.Last() : (int?)null);
        CategoryFilters = (IsFolderDataset ? Array.Empty<DetectionFolderFilter>() : new[] { new DetectionFolderFilter(null, "全部类别", _session.Images.Count, _session.Images.Count(i => i.Status == "approved")) })
            .Concat(_session.Classes.Select(category =>
            {
                var images = _session.Images.Where(image => image.FolderClassId == category.Id ||
                    image.FolderClassId is null && image.ReviewedBoxes.Concat(image.PredictedBoxes).Any(box => box.ClassId == category.Id)).ToArray();
                return new DetectionFolderFilter(category.Id, category.Name, images.Length, images.Count(image => image.Status == "approved"));
            })).ToArray();
        SetProperty(ref _selectedCategory, CategoryFilters.FirstOrDefault(filter => filter.Id == selectedId) ?? CategoryFilters[0], nameof(SelectedCategory));
        _session.SelectedFolderClassId = SelectedCategory?.Id;
        if (IsFolderDataset) _trainedModel = SelectedCategory?.Id is { } activeId ? _session.FolderModels.GetValueOrDefault(activeId) : null;
        bool Matches(DetectionReviewImage image) => SelectedCategory?.Id is not { } id || image.FolderClassId == id ||
            image.FolderClassId is null && image.ReviewedBoxes.Concat(image.PredictedBoxes).Any(box => box.ClassId == id);
        var pending = DetectionActiveLearningStore.Pending(_session).Where(Matches);
        var others = _session.Images.Where(i => i.Status != "pending" && Matches(i))
            .OrderBy(i => i.Status).ThenByDescending(i => i.Priority);
        Items.Clear();
        foreach (var image in pending.Concat(others))
            Items.Add(new DetectionReviewRow(image, _session.Classes.FirstOrDefault(c => c.Id == image.FolderClassId)?.Name));
        SelectedRow = Items.FirstOrDefault(row => row.Image.ImagePath == old)
            ?? Items.FirstOrDefault(row => row.Image.Status == "pending")
            ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(ReviewProgress));
        OnPropertyChanged(nameof(ModelInfo));
        OnPropertyChanged(nameof(ClassesInfo));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(CanAddImages));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanExportAllHalcon));
        OnPropertyChanged(nameof(CombinedDatasetSummary));
        OnPropertyChanged(nameof(CanBootstrap)); OnPropertyChanged(nameof(CanCombinedTrain));
        OnPropertyChanged(nameof(TrainingEligibility));
        foreach (var property in new[] { nameof(Categories), nameof(DatasetName), nameof(SourceDirectory), nameof(ImportIssues),
            nameof(DatasetImageCount), nameof(RootImageCount), nameof(HasTrainedModel), nameof(CanPredictTrained), nameof(CanPredictRemaining),
            nameof(RemainingPredictionCount), nameof(PredictRemainingLabel), nameof(TrainedModel),
            nameof(PredictionMaxBoxes), nameof(PredictionConfidence), nameof(PredictionPolicy), nameof(BulkApprovalCount),
            nameof(CanApproveAll), nameof(ApproveAllLabel), nameof(ThumbnailSummary), nameof(CanParallelTrain) }) OnPropertyChanged(property);
        OnPropertyChanged(nameof(DrawClassId));
        RestoreModelFeedback();
        }
        finally { _refreshingQueue = false; }
    }

    private void UpdatePreview()
    {
        PreviewBoxes = Boxes.Select(box => box.ToBox()).ToArray();
        OnPropertyChanged(nameof(SelectedDetails));
    }

    private void DrawBox(object? parameter)
    {
        if (IsBusy || SelectedRow is null || _session is null || parameter is not DetectionDrawnBox box)
            return;
        var categoryId = DrawClassId;
        var editor = new DetectionBoxEditor(new DetectionBox(categoryId, 0,
            box.Col1, box.Row1, box.Col2, box.Row2));
        editor.PropertyChanged += (_, _) => UpdatePreview();
        Boxes.Add(editor);
        SelectedBox = editor;
        UpdatePreview();
        Status = "检测框已绘制；确认后点击「确认并下一张」，该图像才会用于训练。";
    }

    private void AddBox()
    {
        if (IsBusy || SelectedRow is null || _session is null) return;
        var image = SelectedRow.Image;
        var firstId = DrawClassId;
        var box = new DetectionBoxEditor(new DetectionBox(firstId, 0,
            image.Width * .25, image.Height * .25, image.Width * .75, image.Height * .75));
        box.PropertyChanged += (_, _) => UpdatePreview();
        Boxes.Add(box); SelectedBox = box;
        UpdatePreview();
        Status = "已添加手工框；请修改坐标和类别后点击「接受并下一张」。新增框的模型置信度为0，仅作记录。";
    }

    private void EditBox(object? parameter)
    {
        if (IsBusy || SelectedRow is null || _session is null || parameter is not DetectionBoxEdit edit ||
            edit.Index < 0 || edit.Index >= Boxes.Count) return;
        var editor = Boxes[edit.Index];
        var updated = editor.ToBox() with { Col1 = edit.Col1, Row1 = edit.Row1, Col2 = edit.Col2, Row2 = edit.Row2 };
        DetectionActiveLearningStore.ValidateReviewedBoxes(SelectedRow.Image, [updated], TrainingSession?.Classes ?? _session.Classes);
        editor.Col1 = edit.Col1; editor.Row1 = edit.Row1; editor.Col2 = edit.Col2; editor.Row2 = edit.Row2;
        SelectedBox = editor;
        Status = "检测框已调整；可继续移动或拖动边角。请保存草稿或确认标注。";
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
        if (newStatus == "approved") DetectionActiveLearningStore.ValidateReviewedBoxes(image, boxes, TrainingSession?.Classes ?? _session.Classes);
        image.ReviewedBoxes = boxes;
        image.HasDraft = true;
        image.Status = newStatus;
        await _store.SaveAsync(_session);
        OnPropertyChanged(nameof(CanBootstrap)); OnPropertyChanged(nameof(CanCombinedTrain));
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

    private async Task ApproveAllAsync()
    {
        if (_session is null || HasPredictionParameterErrors) return;
        var snapshot = JsonSerializer.Deserialize<DetectionReviewSession>(JsonSerializer.Serialize(_session))!;
        // Include edits already visible in the current image, rather than approving stale model boxes.
        if (SelectedRow is { Image.Status: "pending" } selected)
        {
            var edits = Boxes.Select(box => box.ToBox()).ToList();
            if (!edits.SequenceEqual(DetectionActiveLearningStore.ReviewBoxes(selected.Image, PredictionMaxBoxes)))
            {
                DetectionActiveLearningStore.ValidateReviewedBoxes(selected.Image, edits, TrainingSession?.Classes ?? snapshot.Classes);
                var current = snapshot.Images.Single(image => image.ImagePath == selected.Image.ImagePath);
                current.ReviewedBoxes = edits; current.HasDraft = true;
            }
        }
        var folderId = IsFolderDataset ? SelectedCategory?.Id : null;
        var candidates = DetectionActiveLearningStore.BulkApprovalCandidates(snapshot, folderId, PredictionMaxBoxes);
        if (candidates.Count == 0) { Status = "当前类别没有可批量确认的有框图片；无框或需重标图片请逐张检查。"; return; }
        var lowScore = candidates.Count(image => !image.HasDraft &&
            DetectionActiveLearningStore.ReviewBoxes(image, PredictionMaxBoxes).Any(box => box.Confidence < .25));
        var prompt = $"确认「{DatasetName}」的 {candidates.Count:N0} 张有框待审核图片？\n\n" +
            (lowScore > 0 ? $"其中 {lowScore:N0} 张预测得分低于 25%，请先核对框的位置。\n" : "") +
            "确认后这些框将作为训练标签。无框、无效框和需重标图片会跳过，并保存操作前备份。";
        if (MessageBox.Show(System.Windows.Application.Current.MainWindow, prompt, "全部确认当前类别",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        { Status = "已取消全部确认，标注状态未改变。"; return; }
        var result = await _store.ApproveAllAsync(snapshot, folderId, PredictionMaxBoxes);
        _session = result.Session;
        RefreshQueue();
        Status = $"{DatasetName} · 已批量确认 {result.Count:N0} 张，可用于下一轮训练；操作前备份：{result.BackupFile}";
    }

    public async Task StopAndWaitAsync()
    {
        _operation?.Cancel();
        while (IsBusy) await Task.Delay(50);
        _operationClock.Stop();
    }

    private async Task ExportAsync()
    {
        if (_session is null) return;
        // The export is intentionally based on persisted, human-approved labels only.
        var scope = TrainingSession ?? throw new InvalidOperationException("请选择一个类别文件夹。");
        var folder = await Task.Run(() => _store.ExportApprovedCocoAsync(scope));
        LastExport = folder;
        Status = "已导出人工确认图片和COCO标注：" + folder;
    }
}
