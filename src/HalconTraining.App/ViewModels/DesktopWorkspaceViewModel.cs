using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HalconTraining.Application.Workspace;
using HalconTraining.Application.Workers;
using HalconTraining.Application.Environment;
using HalconTraining.App.Services;
using HalconTraining.App.Views;

namespace HalconTraining.App.ViewModels;

public sealed class DesktopWorkspaceViewModel : ObservableModel, IDisposable
{
    public const int DatasetPage=0, TrainingPage=1, ModelsPage=2;
    public sealed record EpochRow(int Epoch,double TrainingLoss,double? ValidationIoU,double Seconds,double LearningRate);
    private readonly TrainingWorkspace _workspace;
    private readonly IWorkspaceDialogs _dialogs;
    private readonly ITrainingWorkerClient _environment;
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _operation;
    private readonly DispatcherTimer _clock=new() {Interval=TimeSpan.FromSeconds(.5)};
    private readonly Stopwatch _elapsed=new();
    private readonly Queue<string> _logs=[];
    private DatasetReport? _report;
    private int _inspectedSeed=42;
    private double _inspectedTrainPercent=70,_inspectedValidationPercent=15;
    private bool _advancedExpanded;
    private bool _detectionSelected;
    private bool _busy, _isTraining, _isTesting, _completedTraining, _inputErrors, _parametersExpanded=true, _showAnnotations=true, _checkingEnvironment;
    private string _environmentState="环境待检查", _environmentDetails="尚未检查 HALCON 和 GPU。";
    private bool _environmentHealthy, _environmentAttention;
    private string _status="选择HALCON数据集，开始配置训练。",_dataset="",_task="等待选择数据集",_model="",_datasetSummary="尚未选择数据集",_issues="",_log="",_elapsedText="00:00:00",_loss="—",_epoch="—",_environmentText="HALCON 24.11 · 独立Worker";
    private int _page,_epochs=60,_batch=2,_width=512,_height=512,_seed=42;
    private double _rate=.001,_trainPercent=70,_validationPercent=15,_progress,_opacity=60;
    private DatasetSample? _sample;
    private ModelEntry? _selectedModel;
    private PredictionResult? _result;
    private int _validationInterval=1;
    private string _validationIoU="—",_bestValidationIoU="—",_remainingText="估算中",_learningRateText="—",_iterationText="—",_stageHint="开始训练后显示每轮真实指标。";
    private string? _trainingImage,_trainingMask,_trainingTruth;
    private bool _waiting;
    private double _bestIoU=double.NegativeInfinity;
    private readonly List<TrainingPoint> _trainingPoints=[];
    private readonly List<TrainingPoint> _validationPoints=[];
    private readonly Queue<double> _iterationTimes=[];
    private IReadOnlyList<TrainingPoint> _trainingCurve=[],_validationCurve=[];
    public DesktopWorkspaceViewModel(TrainingWorkspace workspace,IWorkspaceDialogs dialogs,ITrainingWorkerClient environment,IGpuTelemetry gpuTelemetry)
    {
        _workspace=workspace;_dialogs=dialogs;_environment=environment;
        Gpu=new GpuStatusViewModel(gpuTelemetry);
        ActiveLearning=new DetectionActiveLearningViewModel(workspace.Store.Root, new HalconProcessRunner(), new YoloProcessRunner(), () => IsBusy);
        ActiveLearning.PropertyChanged += (_, e) =>
        {
            NotifyCurrentWorkspace();
            if (e.PropertyName == nameof(DetectionActiveLearningViewModel.IsBusy))
            {
                NotifyState();
                if (!ActiveLearning.IsBusy && (ActiveLearning.OperationHeadline.StartsWith("训练完成") ||
                    ActiveLearning.OperationHeadline.StartsWith("并行训练已结束"))) _ = SafeAsync(RefreshModelsAsync);
            }
        };
        BrowseDatasetCommand=new(async token=>{var file=_dialogs.Dataset();if(file is not null)await SelectDatasetAsync(file,token);});
        RepartitionCommand=new(token=>SelectDatasetAsync(DatasetPath,token));
        StartTrainingCommand=new(_=>TrainAsync());
        TestImagesCommand=new(_=>TestAsync(_dialogs.Images()));
        RefreshModelsCommand=new(_=>SafeAsync(RefreshModelsAsync));
        SaveParametersCommand=new(_=>SafeAsync(async()=>{await PersistSessionAsync();Status="训练参数已保存。";}));
        ProbeEnvironmentCommand=new(_=>CheckEnvironmentAsync());
        GoTrainingCommand=new(_=>{if(CanTrain)SelectedPageIndex=TrainingPage;});
        GoModelsCommand=new(_=>{if(!IsBusy)SelectedPageIndex=ModelsPage;});
        PreviousSampleCommand=new(_=>{if(CanPreviousSample)SelectedSample=Samples[Samples.IndexOf(SelectedSample!)-1];});
        NextSampleCommand=new(_=>{if(CanNextSample)SelectedSample=Samples[Samples.IndexOf(SelectedSample!)+1];});
        StopCommand=new(_=>{_operation?.Cancel();Status="正在停止独立Worker，产物保留…";});
        OpenModelFolderCommand=new(_=>OpenFolder(SelectedModel?.Directory));
        OpenResultsFolderCommand=new(_=>OpenFolder(SelectedResult?.OutputDirectory));
        SaveOverlayCommand=new(_=>SaveOverlayAsync());
        _clock.Tick+=(_,_)=>ElapsedText=$"{(int)_elapsed.Elapsed.TotalHours:00}:{_elapsed.Elapsed.Minutes:00}:{_elapsed.Elapsed.Seconds:00}";
    }
    public RelayCommand GoTrainingCommand {get;}
    public RelayCommand GoModelsCommand {get;}
    public RelayCommand PreviousSampleCommand {get;}
    public RelayCommand NextSampleCommand {get;}
    public AsyncCommand BrowseDatasetCommand {get;}
    public AsyncCommand RepartitionCommand {get;}
    public AsyncCommand StartTrainingCommand {get;}
    public AsyncCommand TestImagesCommand {get;}
    public AsyncCommand RefreshModelsCommand {get;}
    public AsyncCommand SaveParametersCommand {get;}
    public AsyncCommand ProbeEnvironmentCommand {get;}
    public AsyncCommand SaveOverlayCommand {get;}
    public RelayCommand StopCommand {get;}
    public RelayCommand OpenModelFolderCommand {get;}
    public RelayCommand OpenResultsFolderCommand {get;}
    public ObservableCollection<DatasetSample> Samples {get;}=[];
    public ObservableCollection<ModelEntry> Models {get;}=[];
    public ObservableCollection<PredictionResult> TestResults {get;}=[];
    public ObservableCollection<EpochRow> EpochMetrics {get;}=[];
    public IReadOnlyList<TrainingPoint> TrainingCurve {get=>_trainingCurve;private set{SetProperty(ref _trainingCurve,value);OnPropertyChanged(nameof(HasTrainingHistory));OnPropertyChanged(nameof(ShowTrainingMonitor));}}
    public bool ShowTrainingMonitor=>IsTraining||HasTrainingHistory||RemainingText is "失败" or "已停止";
    public string StartTrainingLabel=>HasCompletedTraining?"重新训练":"开始训练";
    public bool HasTrainingHistory=>TrainingCurve.Count>0;
    public IReadOnlyList<TrainingPoint> ValidationCurve {get=>_validationCurve;private set=>SetProperty(ref _validationCurve,value);}
    public string ValidationIoU {get=>_validationIoU;private set=>SetProperty(ref _validationIoU,value);}
    public string BestValidationIoU {get=>_bestValidationIoU;private set=>SetProperty(ref _bestValidationIoU,value);}
    public string RemainingText {get=>_remainingText;private set{SetProperty(ref _remainingText,value);OnPropertyChanged(nameof(TrainingStateText));OnPropertyChanged(nameof(ShowTrainingMonitor));}}
    public string LearningRateText {get=>_learningRateText;private set=>SetProperty(ref _learningRateText,value);}
    public string IterationText {get=>_iterationText;private set=>SetProperty(ref _iterationText,value);}
    public string StageHint {get=>_stageHint;private set=>SetProperty(ref _stageHint,value);}
    public bool IsWaiting {get=>_waiting;private set=>SetProperty(ref _waiting,value);}
    public string? TrainingPreviewPath {get=>_trainingImage;private set=>SetProperty(ref _trainingImage,value);}
    public string? TrainingPreviewMask {get=>_trainingMask;private set=>SetProperty(ref _trainingMask,value);}
    public string? TrainingTruthMask {get=>_trainingTruth;private set=>SetProperty(ref _trainingTruth,value);}
    public int ValidationInterval {get=>_validationInterval;set{SetProperty(ref _validationInterval,value);NotifyState();}}
    public DetectionActiveLearningViewModel ActiveLearning {get;}
    public GpuStatusViewModel Gpu {get;}
    public bool HasActiveOperations=>IsBusy||ActiveLearning.IsBusy;
    public string Root=>_workspace.Store.Root;
    public int SelectedPageIndex {get=>_page;set{SetProperty(ref _page,value);NotifyCurrentWorkspace();}}
    public bool DetectionSelected {get=>_detectionSelected;set{SetProperty(ref _detectionSelected,value);NotifyCurrentWorkspace();}}
    private bool ShowDetectionWorkspace=>DetectionSelected&&SelectedPageIndex==DatasetPage;
    public string CurrentTaskName=>ShowDetectionWorkspace?ActiveLearning.DatasetName:TaskName;
    public string CurrentDatasetCaption=>ShowDetectionWorkspace?ActiveLearning.ModelInfo:DatasetCaption;
    public string? CurrentDatasetPath=>ShowDetectionWorkspace?ActiveLearning.SourceDirectory:DatasetPath;
    public string CurrentStatus=>ShowDetectionWorkspace?ActiveLearning.Status:Status;
    private void NotifyCurrentWorkspace(){foreach(var name in new[]{nameof(CurrentTaskName),nameof(CurrentDatasetCaption),nameof(CurrentDatasetPath),nameof(CurrentStatus)})OnPropertyChanged(name);}
    public bool IsBusy {get=>_busy;private set{if(SetProperty(ref _busy,value))NotifyState();}}
    public bool IsIdle=>!HasActiveOperations;
    public bool IsTraining {get=>_isTraining;private set{SetProperty(ref _isTraining,value);AreParametersExpanded=!value;if(value)AreAdvancedParametersExpanded=false;NotifyState();}}
    public bool IsTesting {get=>_isTesting;private set{SetProperty(ref _isTesting,value);NotifyState();}}
    public bool CanStop=>IsTraining||IsTesting;
    public bool HasDataset=>_report is not null;
    public bool HasCompletedTraining {get=>_completedTraining;private set{SetProperty(ref _completedTraining,value);NotifyState();}}
    public bool HasInputErrors {get=>_inputErrors;set{SetProperty(ref _inputErrors,value);NotifyState();}}
    public bool AreParametersExpanded {get=>_parametersExpanded;set{SetProperty(ref _parametersExpanded,value);OnPropertyChanged(nameof(ParametersWidth));}}
    public bool AreAdvancedParametersExpanded {get=>_advancedExpanded;set=>SetProperty(ref _advancedExpanded,IsTraining?false:value);}
    public GridLength ParametersWidth=>new(AreParametersExpanded?350:220);
    public string TrainingStateText=>IsTraining?"训练中":HasCompletedTraining?"已完成":RemainingText is "失败" or "已停止"?RemainingText:"未开始";
    public string? TrainingBlockReason
    {
        get
        {
            if(IsTraining)return "正在训练；参数已锁定，可随时停止。";
            if(IsTesting)return "正在测试原图，请稍后开始训练。";
            if(IsBusy)return "正在读取和检查数据集…";
            if(ActiveLearning.IsBusy)return "目标检测任务正在运行，请完成或停止后训练。";
            if(_report is null)return "先选择 HALCON 数据集。";
            if(_report.Task is not ("semantic" or "classification"))return "已识别此任务，训练尚未接通；当前支持图像分类和语义分割。";
            if(_report.Issues.Any(i=>i.StartsWith("原图不存在：",StringComparison.Ordinal)||i.StartsWith("标注不存在：",StringComparison.Ordinal)))return "图片或标注缺失，请在数据检查中查看文件位置。";
            if(!File.Exists(ModelPath))return "基础模型缺失，请恢复项目 assets/pretrained 中对应的模型副本。";
            if(HasInputErrors)return "参数格式不正确，请检查标红的输入框。";
            if(Epochs<1||Batch<1||ImageWidth<1||ImageHeight<1||ValidationInterval<1||!double.IsFinite(LearningRate)||LearningRate<=0)return "尺寸、Batch、Epoch、学习率和验证间隔必须大于 0。";
            if(!double.IsFinite(TrainPercent)||!double.IsFinite(ValidationPercent)||TrainPercent<=0||ValidationPercent<=0||TrainPercent+ValidationPercent>=100)return "训练、验证、测试集比例必须均大于 0，合计 100%。";
            if(HasPendingSplit)return "数据划分已修改，请点击“更新划分”确认各集合数量。";
            if(_report.SplitCounts.Values.Any(n=>n<=0))return "划分中存在空集合，请调整比例后重新检查。";
            if(_report.SplitCounts.GetValueOrDefault("训练集")<Batch)return "训练样本数量少于 Batch，请减小 Batch。";
            return null;
        }
    }
    public bool CanTrain=>TrainingBlockReason is null;
    public string TrainingReadiness=>TrainingBlockReason??"可以训练 · 数据集和基础模型已匹配。";
    public string DatasetCaption=>_report is null?(IsBusy?"正在读取数据集…":string.IsNullOrWhiteSpace(DatasetPath)?"未选择数据集":"数据集未就绪"):$"{TaskLabel} · {_report.ImageCount:N0} 张图片 · {_report.ClassCount} 类";
    public string ParameterSummary=>$"{ImageWidth} × {ImageHeight}\nBatch  {Batch}\nEpoch  {Epochs}\n学习率  {LearningRate:G4}\n训练中，参数已锁定";
    public bool HasPendingSplit=>_report is not null&&(Seed!=_inspectedSeed||TrainPercent!=_inspectedTrainPercent||ValidationPercent!=_inspectedValidationPercent);
    public bool CanRefreshSplit=>HasDataset&&IsIdle&&!HasInputErrors&&double.IsFinite(TrainPercent)&&double.IsFinite(ValidationPercent)&&TrainPercent>0&&ValidationPercent>0&&TrainPercent+ValidationPercent<100;
    public string SplitRatioSummary=>double.IsFinite(TrainPercent)&&double.IsFinite(ValidationPercent)&&TrainPercent>0&&ValidationPercent>0&&TrainPercent+ValidationPercent<100?$"{TrainPercent:G}% / {ValidationPercent:G}% / {100-TrainPercent-ValidationPercent:G}%"+(HasPendingSplit?" · 待更新":""):"请检查划分比例";
    private string CountText(string name)=>_report is null?"—":HasPendingSplit?"待更新":_report.SplitCounts.GetValueOrDefault(name).ToString("N0");
    public string TrainCountText=>CountText("训练集");
    public string ValidationCountText=>CountText("验证集");
    public string TestCountText=>CountText("测试集");
    public string TaskParameterHeader=>IsClassification?"分类类别与指标":"分割类别与指标";
    public string TaskParameterSummary=>!HasDataset?"选择数据集后显示任务信息。":$"{string.Join("、",Classes)}\n{ValidationMetricLabel}";
    public string BaseModelName=>string.IsNullOrEmpty(ModelPath)?"尚未匹配":Path.GetFileName(ModelPath);
    public IReadOnlyList<string> Classes=>_report?.Classes??[];
    public string DataCheckSummary=>_report is null?"选择后自动检查图片、标注和划分。":_report.Issues.Length==0?"数据检查通过":$"数据检查 · {_report.Issues.Length} 项提示";
    public bool HasMasks=>_report?.Task=="semantic";
    public bool ShowAnnotations {get=>_showAnnotations;set{SetProperty(ref _showAnnotations,value);OnPropertyChanged(nameof(PreviewMaskPath));}}
    public string? PreviewMaskPath=>ShowAnnotations?SelectedSample?.MaskPath:null;
    public string SampleCaption=>SelectedSample is null?"选择数据集后显示图片":$"{Samples.IndexOf(SelectedSample)+1} / {Samples.Count}  ·  {SelectedSample.Name}";
    public string? SampleLabel=>SelectedSample?.Label;
    public bool CanPreviousSample=>SelectedSample is not null&&Samples.IndexOf(SelectedSample)>0;
    public bool CanNextSample=>SelectedSample is not null&&Samples.IndexOf(SelectedSample)<Samples.Count-1;
    public string EnvironmentState {get=>_environmentState;private set=>SetProperty(ref _environmentState,value);}
    public string EnvironmentDetails {get=>_environmentDetails;private set=>SetProperty(ref _environmentDetails,value);}
    public bool EnvironmentHealthy {get=>_environmentHealthy;private set=>SetProperty(ref _environmentHealthy,value);}
    public bool EnvironmentAttention {get=>_environmentAttention;private set=>SetProperty(ref _environmentAttention,value);}
    public string ModelEvaluationSummary=>SelectedModel?.Metrics??"先选择一个已训练模型。";
    public string ModelTestReadiness=>IsTesting?"正在逐张处理原图；可以停止测试。":HasActiveOperations?"当前任务正在运行，请完成后测试。":SelectedModel is null?"先选择模型，再选择原图测试。":SelectedModel.CanTest?"可以测试 · 支持一次选择多张原图。":"此模型尚未完成评估，暂不能测试；请查看状态或重新训练。";
    private void NotifyState()
    {
        NotifyCurrentWorkspace();
        foreach(var name in new[]{nameof(HasActiveOperations),nameof(IsIdle),nameof(CanTrain),nameof(CanTest),nameof(CanStop),nameof(CanSaveOverlay),nameof(TrainingReadiness),nameof(TrainingStateText),nameof(ShowTrainingMonitor),nameof(StartTrainingLabel),nameof(HasDataset),nameof(DatasetCaption),nameof(ParameterSummary),nameof(BaseModelName),nameof(Classes),nameof(DataCheckSummary),nameof(HasMasks),nameof(ModelTestReadiness),nameof(HasPendingSplit),nameof(CanRefreshSplit),nameof(SplitRatioSummary),nameof(TrainCountText),nameof(ValidationCountText),nameof(TestCountText),nameof(TaskParameterHeader),nameof(TaskParameterSummary)})OnPropertyChanged(name);
    }
    public bool CanTest=>!HasActiveOperations&&SelectedModel?.CanTest==true;
    public string Status {get=>_status;private set{SetProperty(ref _status,value);OnPropertyChanged(nameof(CurrentStatus));}}
    public string DatasetPath {get=>_dataset;private set{if(SetProperty(ref _dataset,value))OnPropertyChanged(nameof(TaskName));}}
    public string TaskName=>string.IsNullOrWhiteSpace(DatasetPath)?"未选择数据集":Path.GetFileNameWithoutExtension(DatasetPath);
    public bool IsClassification=>_report?.Task=="classification";
    public string ValidationMetricLabel=>IsClassification?"验证准确率":"验证 mIoU";
    public string BestMetricLabel=>IsClassification?"最佳验证准确率":"最佳验证 mIoU";
    private string _classificationPreview="";
    public string ClassificationPreview {get=>_classificationPreview;private set=>SetProperty(ref _classificationPreview,value);}
    public string TaskLabel {get=>_task;private set{SetProperty(ref _task,value);OnPropertyChanged(nameof(IsClassification));OnPropertyChanged(nameof(ValidationMetricLabel));OnPropertyChanged(nameof(BestMetricLabel));}}
    public string ModelPath {get=>_model;private set{SetProperty(ref _model,value);NotifyState();}}
    public string DatasetSummary {get=>_datasetSummary;private set=>SetProperty(ref _datasetSummary,value);}
    public string Issues {get=>_issues;private set=>SetProperty(ref _issues,value);}
    public string EnvironmentText {get=>_environmentText;private set=>SetProperty(ref _environmentText,value);}
    public string LogText {get=>_log;private set=>SetProperty(ref _log,value);}
    public string ElapsedText {get=>_elapsedText;private set=>SetProperty(ref _elapsedText,value);}
    public string Loss {get=>_loss;private set=>SetProperty(ref _loss,value);}
    public string Epoch {get=>_epoch;private set=>SetProperty(ref _epoch,value);}
    public double Progress {get=>_progress;private set=>SetProperty(ref _progress,value);}
    public int Epochs {get=>_epochs;set{SetProperty(ref _epochs,value);NotifyState();}}
    public int Batch {get=>_batch;set{SetProperty(ref _batch,value);NotifyState();}}
    public int ImageWidth {get=>_width;set{SetProperty(ref _width,value);NotifyState();}}
    public int ImageHeight {get=>_height;set{SetProperty(ref _height,value);NotifyState();}}
    public int Seed {get=>_seed;set{SetProperty(ref _seed,value);NotifyState();}}
    public double LearningRate {get=>_rate;set{SetProperty(ref _rate,value);NotifyState();}}
    public double TrainPercent {get=>_trainPercent;set{SetProperty(ref _trainPercent,value);NotifyState();}}
    public double ValidationPercent {get=>_validationPercent;set{SetProperty(ref _validationPercent,value);NotifyState();}}
    public double OverlayOpacity {get=>_opacity;set=>SetProperty(ref _opacity,value);}
    public DatasetSample? SelectedSample {get=>_sample;set{if(SetProperty(ref _sample,value)){foreach(var name in new[]{nameof(PreviewPath),nameof(PreviewMaskPath),nameof(SampleCaption),nameof(SampleLabel),nameof(CanPreviousSample),nameof(CanNextSample)})OnPropertyChanged(name);}}}
    public string? PreviewPath=>SelectedSample?.Missing==false?SelectedSample.Path:null;
    public ModelEntry? SelectedModel {get=>_selectedModel;set{if(SetProperty(ref _selectedModel,value)){OnPropertyChanged(nameof(CanTest));OnPropertyChanged(nameof(ModelSummary));OnPropertyChanged(nameof(TestResultTitle));OnPropertyChanged(nameof(IsClassificationModel));OnPropertyChanged(nameof(OriginalImageWidth));OnPropertyChanged(nameof(ModelEvaluationSummary));OnPropertyChanged(nameof(ModelTestReadiness));_ = SafeAsync(RestoreResultsAsync);}}}
    public string ModelSummary=>SelectedModel is null?"选择模型以查看真实评估和测试结果。":SelectedModel.Metrics+Environment.NewLine+SelectedModel.Directory;
    public PredictionResult? SelectedResult {get=>_result;set{if(SetProperty(ref _result,value)){OnPropertyChanged(nameof(TestOriginalPath));OnPropertyChanged(nameof(TestMaskPath));OnPropertyChanged(nameof(ResultSummary));OnPropertyChanged(nameof(CanSaveOverlay));OnPropertyChanged(nameof(ClassificationScores));OnPropertyChanged(nameof(TestCaption));}}}
    public string? TestOriginalPath=>SelectedResult?.OriginalPath;
    public string? TestMaskPath=>SelectedResult?.MaskPath;
    public GridLength OriginalImageWidth=>new(IsClassificationModel?2:1,GridUnitType.Star);
    public bool IsClassificationModel=>SelectedModel?.Task=="classification";
    public string? TestCaption=>SelectedResult?.Task=="classification"?$"{SelectedResult.ClassName}   {SelectedResult.Confidence:P2}":null;
    public bool CanSaveOverlay=>IsIdle&&SelectedResult?.MaskPath is not null;
    public string TestResultTitle=>SelectedModel?.Task=="classification"?"分类结果 · 按置信度排序":"处理结果 · 青色为预测区域";
    public IReadOnlyList<ClassificationScore> ClassificationScores=>SelectedResult?.Scores??[];
    public string ResultSummary=>SelectedResult is null?"直接选择原图，无需测试字典或人工标注。":SelectedResult.Task=="classification"?$"{SelectedResult.File} · 预测：{SelectedResult.ClassName} · 置信度 {SelectedResult.Confidence:P2} · 推理 {SelectedResult.InferenceMs:F1} ms":$"{SelectedResult.File} · {SelectedResult.Width} × {SelectedResult.Height} · 推理 {SelectedResult.InferenceMs:F1} ms · 前景 {SelectedResult.Areas.Where(a=>a.ClassId!=0).Sum(a=>a.Ratio):P2}";
    public async Task InitializeAsync()
    {
        Gpu.Start();
        await ActiveLearning.InitializeAsync();
        await SafeAsync(async()=>
        {
            var session=await _workspace.Store.LoadSessionAsync();
            if(session?.Settings is { } s){Epochs=s.Epochs;Batch=s.Batch;ImageWidth=s.Width;ImageHeight=s.Height;LearningRate=s.Rate;Seed=s.Seed;TrainPercent=s.TrainPercent;ValidationPercent=s.ValidationPercent;ValidationInterval=Math.Max(1,s.ValidationInterval);}
            await RefreshModelsAsync();
            SelectedModel=Models.FirstOrDefault(m=>m.Directory==session?.SelectedModel)??Models.FirstOrDefault(m=>m.CanTest);
            if(session?.Dataset is { } file&&File.Exists(file))await SelectDatasetAsync(file,_lifetime.Token);
            await CheckEnvironmentAsync();
        });
    }
    public async Task SelectDatasetAsync(string file,CancellationToken token)
    {
        if(IsBusy||string.IsNullOrWhiteSpace(file))return;
        IsBusy=true;
        try
        {
            if(!string.Equals(DatasetPath,Path.GetFullPath(file),StringComparison.OrdinalIgnoreCase))ResetTrainingDisplay();
            DatasetPath=Path.GetFullPath(file);_report=null;ModelPath="";
            Status="正在读取原始数据集与自动划分…";
            var report=await _workspace.InspectAsync(file,Seed,TrainPercent,ValidationPercent,token);
            _report=report;_inspectedSeed=Seed;_inspectedTrainPercent=TrainPercent;_inspectedValidationPercent=ValidationPercent;DatasetPath=Path.GetFullPath(file);TaskLabel=Label(report.Task);
            DatasetSummary=$"{report.ImageCount:N0} 张图像 · {report.ClassCount} 类 · "+string.Join(" / ",report.SplitCounts.Select(pair=>$"{pair.Key} {pair.Value}"));
            Issues=string.Join(Environment.NewLine,report.Issues);
            Samples.Clear();foreach(var sample in report.Samples)Samples.Add(sample);SelectedSample=Samples.FirstOrDefault();
            await MatchModelAsync(report.Task);
            Status=report.Task is "semantic" or "classification"?"读取完成，数据集、任务和基础模型已匹配。":"读取完成；该任务的训练适配器尚未验收。";
            await PersistSessionAsync();
        }
        catch(Exception error){_report=null;Samples.Clear();SelectedSample=null;DatasetSummary="读取失败";Status=error.Message;AppendLog("数据集："+error.Message);}
        finally{IsBusy=false;NotifyState();}
    }
    private async Task MatchModelAsync(string task)
    {
        var key=task switch {"semantic"=>"semantic_segmentation","multi"=>"multilabel_classification","instance"=>"instance_segmentation","rotated"=>"rotated_detection","anomaly"=>"anomaly_detection","ocr"=>"ocr_detection",_=>task};
        using var catalog=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Root,"assets","pretrained","catalog.json")));
        ModelPath=catalog.RootElement.GetProperty("taskDefaults").TryGetProperty(key,out var model)?Path.Combine(Root,"assets","pretrained",model.GetProperty("file").GetString()!):"";
        if(!File.Exists(ModelPath))Status="项目内基础模型缺失，请恢复assets/pretrained模型副本。";
    }
    private TrainingSettings Settings()=>new(DatasetPath,ModelPath,Epochs,Batch,ImageWidth,ImageHeight,LearningRate,Seed,TrainPercent,ValidationPercent,ValidationInterval,_report?.Task=="classification"?"classification":"semantic_segmentation");
    private Task PersistSessionAsync()=>_workspace.Store.SaveSessionAsync(new(DatasetPath,Settings(),SelectedModel?.Directory));
    public async Task CheckEnvironmentAsync()
    {
        if(_checkingEnvironment)return;
        _checkingEnvironment=true;EnvironmentState="正在检查环境…";
        try
        {
            var report=await _environment.CaptureEnvironmentAsync(_lifetime.Token);
            EnvironmentText=$"HALCON {report.HalconVersion} · {report.GpuName}";
            EnvironmentDetails=EnvironmentText+Environment.NewLine+string.Join(Environment.NewLine,report.Checks.Select(c=>$"{c.Name}：{c.Summary}"))+Environment.NewLine+"训练许可与实际任务兼容性以真实训练结果为准。";
            EnvironmentHealthy=report.CanUseHalconRuntime&&report.HalconDlDevices.Count>0;
            EnvironmentAttention=report.Checks.Any(c=>c.Status is HalconTraining.Domain.Training.CapabilityStatus.Failed or HalconTraining.Domain.Training.CapabilityStatus.Unsupported);
            EnvironmentState=EnvironmentAttention?"环境需确认":EnvironmentHealthy?"HALCON / GPU 已识别":"环境未就绪";
        }
        catch(OperationCanceledException){EnvironmentState="环境检查已取消";}
        catch(Exception error){EnvironmentState="环境检查失败";EnvironmentAttention=true;EnvironmentDetails=error.Message;}
        finally{_checkingEnvironment=false;}
    }
    private void ResetTrainingDisplay()
    {
        HasCompletedTraining=false;_trainingPoints.Clear();_validationPoints.Clear();_iterationTimes.Clear();EpochMetrics.Clear();TrainingCurve=[];ValidationCurve=[];
        Loss="—";Epoch="—";Progress=0;ValidationIoU="—";BestValidationIoU="—";RemainingText="估算中";ElapsedText="00:00:00";
        StageHint="开始训练后显示实时曲线和进度。";TrainingPreviewPath=null;TrainingPreviewMask=null;TrainingTruthMask=null;ClassificationPreview="";AreParametersExpanded=true;
    }
    public async Task TrainAsync()
    {
        if(!CanTrain){Status=TrainingReadiness;return;}
        // Re-inspection ensures edited split settings are the settings used by training.
        await SelectDatasetAsync(DatasetPath,_lifetime.Token);
        if(!CanTrain){Status=TrainingReadiness;return;}
        if(Epochs<1||Batch<1||ImageWidth<1||ImageHeight<1||ValidationInterval<1||!double.IsFinite(LearningRate)||LearningRate<=0){Status="训练参数必须为有效正数。";return;}
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_operation=operation;IsTraining=true;IsBusy=true;HasCompletedTraining=false;AreParametersExpanded=false;
        _logs.Clear();LogText="";Loss="—";Epoch="—";Progress=0;
        _trainingPoints.Clear();_validationPoints.Clear();_iterationTimes.Clear();EpochMetrics.Clear();TrainingCurve=[];ValidationCurve=[];
        ValidationIoU="—";BestValidationIoU="—";_bestIoU=double.NegativeInfinity;RemainingText="估算中";IterationText="—";
        ClassificationPreview="";LearningRateText=LearningRate.ToString("G4",CultureInfo.InvariantCulture);TrainingPreviewPath=null;TrainingPreviewMask=null;TrainingTruthMask=null;
        _elapsed.Restart();_clock.Start();SelectedPageIndex=TrainingPage;Status="正在初始化训练…";
        try
        {
            var progress=new Progress<TrainingProgress>(event_=>
            {
                Status=event_.Stage;AppendLog(event_.Stage+" · "+event_.Detail);
                IsWaiting=event_.Stage is "加载模型" or "初始化GPU" or "轮次验证" or "独立评估";
                if(event_.LearningRate is { } rate)LearningRateText=rate.ToString("G4",CultureInfo.InvariantCulture);
                if(event_.Stage=="训练"&&event_.Loss is { } loss)
                {
                    EnvironmentHealthy=true;EnvironmentAttention=false;EnvironmentState="HALCON / GPU 已验证";
                    var x=event_.Epoch-1+(double)event_.Batch/Math.Max(1,event_.Batches);
                    var mean=event_.MeanLoss??loss;Loss=mean.ToString("F5",CultureInfo.InvariantCulture);Epoch=$"{event_.Epoch} / {event_.Epochs}";Progress=100*x/event_.Epochs;
                    _trainingPoints.Add(new(x,mean));
                    if(_trainingPoints.Count>1500){var reduced=_trainingPoints.Where((_,i)=>i%2==0||i==_trainingPoints.Count-1).ToArray();_trainingPoints.Clear();_trainingPoints.AddRange(reduced);}
                    TrainingCurve=_trainingPoints.ToArray();
                    var completed=(event_.Epoch-1)*event_.Batches+event_.Batch;
                    IterationText=$"{completed} / {event_.Epochs*event_.Batches}";
                    Status=$"正在训练 · 第{event_.Epoch}轮 · 批次 {event_.Batch} / {event_.Batches}";
                    if(event_.IterationMs is { } ms){_iterationTimes.Enqueue(ms);while(_iterationTimes.Count>20)_iterationTimes.Dequeue();}
                    if(_iterationTimes.Count>=3)
                    {
                        var ordered=_iterationTimes.Order().ToArray();var typical=ordered[ordered.Length/2];
                        var seconds=(event_.Epochs*event_.Batches-completed)*typical/1000;
                        RemainingText=TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
                        StageHint=$"学习率 {LearningRateText} · Batch {Batch} · {Batch*1000/Math.Max(1,typical):F1} 张/秒 · 剩余时间按近期训练批次估算，不含后续验证";
                    }
                }
                else if(event_.Stage=="轮次完成")
                {
                    var score=IsClassification?event_.ValidationAccuracy:event_.ValidationIoU;
                    EpochMetrics.Add(new(event_.Epoch,event_.Loss??0,score,event_.EpochSeconds??0,event_.LearningRate??LearningRate));
                    if(score is { } iou){ValidationIoU=iou.ToString("P2");_bestIoU=Math.Max(_bestIoU,iou);BestValidationIoU=_bestIoU.ToString("P2");_validationPoints.Add(new(event_.Epoch,iou));ValidationCurve=_validationPoints.ToArray();}
                    if(event_.PreviewImage is not null){TrainingPreviewPath=event_.PreviewImage;TrainingPreviewMask=event_.PreviewMask;TrainingTruthMask=event_.TruthMask;}
                    if(event_.PredictedLabel is not null)ClassificationPreview=$"人工真值：{event_.TruthLabel}    预测：{event_.PredictedLabel}    置信度：{event_.Confidence:P2}";
                    StageHint=$"第{event_.Epoch}轮结束 · 训练Loss {event_.Loss:F5} · {ValidationMetricLabel} {ValidationIoU} · 本轮 {event_.EpochSeconds:F1} 秒";
                }
                else if(event_.Stage=="轮次验证"){StageHint=$"第{event_.Epoch}轮验证：计算{ValidationMetricLabel}和样本预测，模型权重不更新。";}
                else if(event_.Stage=="独立评估"){RemainingText="评估中";StageHint="训练已结束，重新加载模型完成独立验证集与测试集评估。";}
                else if(event_.Stage=="初始化GPU"){StageHint="正在初始化GPU；首批计算可能需要预热，指标产生后实时更新。";}
            });
            var directory=await _workspace.TrainAsync(Settings(),progress,operation.Token);
            HasCompletedTraining=true;EnvironmentState="训练验证通过";Status="训练与独立评估完成，已归档到模型库。";Progress=100;RemainingText="已完成";
            await RefreshModelsAsync();SelectedModel=Models.First(m=>m.Directory==directory);await PersistSessionAsync();
        }
        catch(OperationCanceledException){Status="训练已停止，部分产物保留，未发布为已验证模型。";StageHint=Status;EnvironmentState=EnvironmentHealthy?"HALCON / GPU 可用":"环境待确认";RemainingText="已停止";await RefreshModelsAsync();}
        catch(Exception error){HasCompletedTraining=false;Status="训练失败："+error.Message;StageHint=Status;EnvironmentState="本次训练失败";EnvironmentHealthy=false;EnvironmentAttention=true;EnvironmentDetails=Status;RemainingText="失败";AppendLog(Status);await RefreshModelsAsync();}
        finally{_elapsed.Stop();_clock.Stop();_operation=null;IsWaiting=false;IsTraining=false;IsBusy=false;}
    }
    public async Task TestAsync(IReadOnlyList<string> files)
    {
        if(!CanTest||files.Count==0)return;
        if(files.Count>100){Status="每批最多选择100张原图。";return;}
        var model=SelectedModel!;using var operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_operation=operation;IsTesting=true;IsBusy=true;
        var succeeded=0;var failed=0;
        try
        {
            for(var i=0;i<files.Count;i++)
            {
                operation.Token.ThrowIfCancellationRequested();Status=$"正在测试 {i+1} / {files.Count}：{Path.GetFileName(files[i])}";
                try{var result=await _workspace.TestAsync(files[i],model,operation.Token);TestResults.Insert(0,result);SelectedResult=result;succeeded++;}
                catch(OperationCanceledException){throw;}
                catch(Exception error){failed++;AppendLog(Path.GetFileName(files[i])+"："+error.Message);}
            }
            Status=$"测试结束：成功 {succeeded}，失败 {failed}。"+(failed>0?" 可展开详细信息查看错误原因。":"");await PersistSessionAsync();
        }
        catch(OperationCanceledException){Status=$"测试已停止：成功 {succeeded}，失败 {failed}。";}
        finally{_operation=null;IsTesting=false;IsBusy=false;}
    }
    public async Task SaveOverlayAsync()
    {
        if(!CanSaveOverlay||SelectedResult is null)return;
        await SafeAsync(async()=>{await _workspace.SaveOverlayAsync(SelectedResult,OverlayOpacity/100,_lifetime.Token);Status="叠加图已保存："+Path.Combine(SelectedResult.OutputDirectory,"overlay.png");});
    }
    public async Task RefreshModelsAsync()
    {
        var selected=SelectedModel?.Directory;var models=await _workspace.Store.ModelsAsync();Models.Clear();foreach(var model in models)Models.Add(model);
        SelectedModel=Models.FirstOrDefault(m=>m.Directory==selected)??Models.FirstOrDefault(m=>m.CanTest);
    }
    private async Task RestoreResultsAsync()
    {
        var model=SelectedModel;TestResults.Clear();SelectedResult=null;if(model is null)return;
        var results=await _workspace.Store.ResultsAsync(model.Directory);
        if(SelectedModel?.Directory!=model.Directory)return;
        foreach(var result in results)TestResults.Add(result);SelectedResult=TestResults.FirstOrDefault();
    }
    private void AppendLog(string text){_logs.Enqueue($"{DateTime.Now:HH:mm:ss} {text}");while(_logs.Count>300)_logs.Dequeue();LogText=string.Join(Environment.NewLine,_logs);}
    private async Task SafeAsync(Func<Task> action){try{await action();}catch(Exception error){Status=error.Message;AppendLog(error.Message);}}
    private static void OpenFolder(string? directory){if(directory is not null&&Directory.Exists(directory))Process.Start(new ProcessStartInfo(directory){UseShellExecute=true});}
    private static string Label(string task)=>task switch {"semantic"=>"语义分割","detection"=>"目标检测 · 水平框","rotated"=>"目标检测 · 旋转框","instance"=>"实例分割","classification"=>"图像分类","multi"=>"多标签分类","anomaly"=>"异常检测","ocr"=>"OCR · 需确认组件",_=>"无法识别任务"};
    public void RequestStop()=>_operation?.Cancel();
    public async Task StopAndWaitAsync(){_lifetime.Cancel();_operation?.Cancel();await ActiveLearning.StopAndWaitAsync();while(IsBusy)await Task.Delay(50);}
    public void Dispose(){Gpu.Dispose();_clock.Stop();_lifetime.Cancel();_lifetime.Dispose();}
}
