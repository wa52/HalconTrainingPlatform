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
using HalconTraining.App.Services;
using HalconTraining.App.Views;

namespace HalconTraining.App.ViewModels;

public sealed class DesktopWorkspaceViewModel : ObservableModel, IDisposable
{
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
    private bool _busy;
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
    public DesktopWorkspaceViewModel(TrainingWorkspace workspace,IWorkspaceDialogs dialogs,ITrainingWorkerClient environment)
    {
        _workspace=workspace;_dialogs=dialogs;_environment=environment;
        ActiveLearning=new DetectionActiveLearningViewModel(workspace.Store.Root, new HalconProcessRunner(), () => IsBusy);
        BrowseDatasetCommand=new(async token=>{var file=_dialogs.Dataset();if(file is not null)await SelectDatasetAsync(file,token);});
        RepartitionCommand=new(token=>SelectDatasetAsync(DatasetPath,token));
        StartTrainingCommand=new(_=>TrainAsync());
        TestImagesCommand=new(_=>TestAsync(_dialogs.Images()));
        RefreshModelsCommand=new(_=>SafeAsync(RefreshModelsAsync));
        SaveParametersCommand=new(_=>SafeAsync(async()=>{await PersistSessionAsync();Status="训练参数已保存。";}));
        ProbeEnvironmentCommand=new(token=>SafeAsync(async()=>{var report=await _environment.CaptureEnvironmentAsync(token);EnvironmentText=$"HALCON {report.HalconVersion} · {report.GpuName}";Status=string.Join(Environment.NewLine,report.Checks.Select(c=>$"{c.Name}：{c.Summary}"));}));
        StopCommand=new(_=>{_operation?.Cancel();Status="正在停止独立Worker，产物保留…";});
        OpenModelFolderCommand=new(_=>OpenFolder(SelectedModel?.Directory));
        OpenResultsFolderCommand=new(_=>OpenFolder(SelectedResult?.OutputDirectory));
        SaveOverlayCommand=new(_=>SaveOverlayAsync());
        _clock.Tick+=(_,_)=>ElapsedText=$"{(int)_elapsed.Elapsed.TotalHours:00}:{_elapsed.Elapsed.Minutes:00}:{_elapsed.Elapsed.Seconds:00}";
    }
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
    public IReadOnlyList<TrainingPoint> TrainingCurve {get=>_trainingCurve;private set=>SetProperty(ref _trainingCurve,value);}
    public IReadOnlyList<TrainingPoint> ValidationCurve {get=>_validationCurve;private set=>SetProperty(ref _validationCurve,value);}
    public string ValidationIoU {get=>_validationIoU;private set=>SetProperty(ref _validationIoU,value);}
    public string BestValidationIoU {get=>_bestValidationIoU;private set=>SetProperty(ref _bestValidationIoU,value);}
    public string RemainingText {get=>_remainingText;private set=>SetProperty(ref _remainingText,value);}
    public string LearningRateText {get=>_learningRateText;private set=>SetProperty(ref _learningRateText,value);}
    public string IterationText {get=>_iterationText;private set=>SetProperty(ref _iterationText,value);}
    public string StageHint {get=>_stageHint;private set=>SetProperty(ref _stageHint,value);}
    public bool IsWaiting {get=>_waiting;private set=>SetProperty(ref _waiting,value);}
    public string? TrainingPreviewPath {get=>_trainingImage;private set=>SetProperty(ref _trainingImage,value);}
    public string? TrainingPreviewMask {get=>_trainingMask;private set=>SetProperty(ref _trainingMask,value);}
    public string? TrainingTruthMask {get=>_trainingTruth;private set=>SetProperty(ref _trainingTruth,value);}
    public int ValidationInterval {get=>_validationInterval;set=>SetProperty(ref _validationInterval,value);}
    public DetectionActiveLearningViewModel ActiveLearning {get;}
    public string Root=>_workspace.Store.Root;
    public int SelectedPageIndex {get=>_page;set=>SetProperty(ref _page,value);}
    public bool IsBusy {get=>_busy;private set{if(SetProperty(ref _busy,value)){OnPropertyChanged(nameof(IsIdle));OnPropertyChanged(nameof(CanTrain));OnPropertyChanged(nameof(CanTest));OnPropertyChanged(nameof(CanSaveOverlay));}}}
    public bool IsIdle=>!IsBusy;
    public bool CanTrain=>!IsBusy&&(_report?.Task is "semantic" or "classification")&&File.Exists(ModelPath)&&!_report.Issues.Any(issue=>issue.StartsWith("原图不存在：",StringComparison.Ordinal))&&_report.Samples.All(s=>!s.Missing)&&_report.SplitCounts.Values.All(n=>n>0);
    public bool CanTest=>!IsBusy&&SelectedModel?.CanTest==true;
    public string Status {get=>_status;private set=>SetProperty(ref _status,value);}
    public string DatasetPath {get=>_dataset;private set{if(SetProperty(ref _dataset,value))OnPropertyChanged(nameof(TaskName));}}
    public string TaskName=>string.IsNullOrWhiteSpace(DatasetPath)?"未选择数据集":Path.GetFileNameWithoutExtension(DatasetPath);
    public bool IsClassification=>_report?.Task=="classification";
    public string ValidationMetricLabel=>IsClassification?"验证准确率":"验证 mIoU";
    public string BestMetricLabel=>IsClassification?"最佳验证准确率":"最佳验证 mIoU";
    private string _classificationPreview="";
    public string ClassificationPreview {get=>_classificationPreview;private set=>SetProperty(ref _classificationPreview,value);}
    public string TaskLabel {get=>_task;private set{SetProperty(ref _task,value);OnPropertyChanged(nameof(IsClassification));OnPropertyChanged(nameof(ValidationMetricLabel));OnPropertyChanged(nameof(BestMetricLabel));}}
    public string ModelPath {get=>_model;private set=>SetProperty(ref _model,value);}
    public string DatasetSummary {get=>_datasetSummary;private set=>SetProperty(ref _datasetSummary,value);}
    public string Issues {get=>_issues;private set=>SetProperty(ref _issues,value);}
    public string EnvironmentText {get=>_environmentText;private set=>SetProperty(ref _environmentText,value);}
    public string LogText {get=>_log;private set=>SetProperty(ref _log,value);}
    public string ElapsedText {get=>_elapsedText;private set=>SetProperty(ref _elapsedText,value);}
    public string Loss {get=>_loss;private set=>SetProperty(ref _loss,value);}
    public string Epoch {get=>_epoch;private set=>SetProperty(ref _epoch,value);}
    public double Progress {get=>_progress;private set=>SetProperty(ref _progress,value);}
    public int Epochs {get=>_epochs;set=>SetProperty(ref _epochs,value);}
    public int Batch {get=>_batch;set=>SetProperty(ref _batch,value);}
    public int ImageWidth {get=>_width;set=>SetProperty(ref _width,value);}
    public int ImageHeight {get=>_height;set=>SetProperty(ref _height,value);}
    public int Seed {get=>_seed;set=>SetProperty(ref _seed,value);}
    public double LearningRate {get=>_rate;set=>SetProperty(ref _rate,value);}
    public double TrainPercent {get=>_trainPercent;set=>SetProperty(ref _trainPercent,value);}
    public double ValidationPercent {get=>_validationPercent;set=>SetProperty(ref _validationPercent,value);}
    public double OverlayOpacity {get=>_opacity;set=>SetProperty(ref _opacity,value);}
    public DatasetSample? SelectedSample {get=>_sample;set{if(SetProperty(ref _sample,value))OnPropertyChanged(nameof(PreviewPath));}}
    public string? PreviewPath=>SelectedSample?.Missing==false?SelectedSample.Path:null;
    public ModelEntry? SelectedModel {get=>_selectedModel;set{if(SetProperty(ref _selectedModel,value)){OnPropertyChanged(nameof(CanTest));OnPropertyChanged(nameof(ModelSummary));OnPropertyChanged(nameof(TestResultTitle));OnPropertyChanged(nameof(IsClassificationModel));_ = SafeAsync(RestoreResultsAsync);}}}
    public string ModelSummary=>SelectedModel is null?"选择模型以查看真实评估和测试结果。":SelectedModel.Metrics+Environment.NewLine+SelectedModel.Directory;
    public PredictionResult? SelectedResult {get=>_result;set{if(SetProperty(ref _result,value)){OnPropertyChanged(nameof(TestOriginalPath));OnPropertyChanged(nameof(TestMaskPath));OnPropertyChanged(nameof(ResultSummary));OnPropertyChanged(nameof(CanSaveOverlay));OnPropertyChanged(nameof(ClassificationScores));OnPropertyChanged(nameof(TestCaption));}}}
    public string? TestOriginalPath=>SelectedResult?.OriginalPath;
    public string? TestMaskPath=>SelectedResult?.MaskPath;
    public bool IsClassificationModel=>SelectedModel?.Task=="classification";
    public string? TestCaption=>SelectedResult?.Task=="classification"?$"{SelectedResult.ClassName}   {SelectedResult.Confidence:P2}":null;
    public bool CanSaveOverlay=>IsIdle&&SelectedResult?.MaskPath is not null;
    public string TestResultTitle=>SelectedModel?.Task=="classification"?"分类结果 · 按置信度排序":"处理结果 · 青色为预测区域";
    public IReadOnlyList<ClassificationScore> ClassificationScores=>SelectedResult?.Scores??[];
    public string ResultSummary=>SelectedResult is null?"直接选择原图，无需测试字典或人工标注。":SelectedResult.Task=="classification"?$"{SelectedResult.File} · 预测：{SelectedResult.ClassName} · 置信度 {SelectedResult.Confidence:P2} · 推理 {SelectedResult.InferenceMs:F1} ms":$"{SelectedResult.File} · {SelectedResult.Width} × {SelectedResult.Height} · 推理 {SelectedResult.InferenceMs:F1} ms · 前景 {SelectedResult.Areas.Where(a=>a.ClassId!=0).Sum(a=>a.Ratio):P2}";
    public async Task InitializeAsync()
    {
        await ActiveLearning.InitializeAsync();
        await SafeAsync(async()=>
        {
            var session=await _workspace.Store.LoadSessionAsync();
            if(session?.Settings is { } s){Epochs=s.Epochs;Batch=s.Batch;ImageWidth=s.Width;ImageHeight=s.Height;LearningRate=s.Rate;Seed=s.Seed;TrainPercent=s.TrainPercent;ValidationPercent=s.ValidationPercent;ValidationInterval=Math.Max(1,s.ValidationInterval);}
            await RefreshModelsAsync();
            SelectedModel=Models.FirstOrDefault(m=>m.Directory==session?.SelectedModel)??Models.FirstOrDefault(m=>m.CanTest);
            if(session?.Dataset is { } file&&File.Exists(file))await SelectDatasetAsync(file,_lifetime.Token);
        });
    }
    public async Task SelectDatasetAsync(string file,CancellationToken token)
    {
        if(IsBusy||string.IsNullOrWhiteSpace(file))return;
        IsBusy=true;
        try
        {
            DatasetPath=Path.GetFullPath(file);_report=null;ModelPath="";
            Status="正在读取原始数据集与自动划分…";
            var report=await _workspace.InspectAsync(file,Seed,TrainPercent,ValidationPercent,token);
            _report=report;DatasetPath=Path.GetFullPath(file);TaskLabel=Label(report.Task);
            DatasetSummary=$"{report.ImageCount:N0} 张图像 · {report.ClassCount} 类 · "+string.Join(" / ",report.SplitCounts.Select(pair=>$"{pair.Key} {pair.Value}"));
            Issues=string.Join(Environment.NewLine,report.Issues);
            Samples.Clear();foreach(var sample in report.Samples)Samples.Add(sample);SelectedSample=Samples.FirstOrDefault();
            await MatchModelAsync(report.Task);
            Status=report.Task is "semantic" or "classification"?"读取完成，数据集、任务和基础模型已匹配。":"读取完成；该任务的训练适配器尚未验收。";
            await PersistSessionAsync();
        }
        catch(Exception error){_report=null;Samples.Clear();SelectedSample=null;DatasetSummary="读取失败";Status=error.Message;AppendLog("数据集："+error.Message);}
        finally{IsBusy=false;}
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
    public async Task TrainAsync()
    {
        if(!CanTrain)return;
        // Re-inspection ensures edited split settings are the settings used by training.
        await SelectDatasetAsync(DatasetPath,_lifetime.Token);
        if(!CanTrain)return;
        if(Epochs<1||Batch<1||ImageWidth<1||ImageHeight<1||ValidationInterval<1||!double.IsFinite(LearningRate)||LearningRate<=0){Status="训练参数必须为有效正数。";return;}
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_operation=operation;IsBusy=true;
        _logs.Clear();LogText="";Loss="—";Epoch="—";Progress=0;
        _trainingPoints.Clear();_validationPoints.Clear();_iterationTimes.Clear();EpochMetrics.Clear();TrainingCurve=[];ValidationCurve=[];
        ValidationIoU="—";BestValidationIoU="—";_bestIoU=double.NegativeInfinity;RemainingText="估算中";IterationText="—";
        ClassificationPreview="";LearningRateText=LearningRate.ToString("G4",CultureInfo.InvariantCulture);TrainingPreviewPath=null;TrainingPreviewMask=null;TrainingTruthMask=null;
        _elapsed.Restart();_clock.Start();SelectedPageIndex=2;Status="正在初始化训练…";
        try
        {
            var progress=new Progress<TrainingProgress>(event_=>
            {
                Status=event_.Stage;AppendLog(event_.Stage+" · "+event_.Detail);
                IsWaiting=event_.Stage is "加载模型" or "初始化GPU" or "轮次验证" or "独立评估";
                if(event_.LearningRate is { } rate)LearningRateText=rate.ToString("G4",CultureInfo.InvariantCulture);
                if(event_.Stage=="训练"&&event_.Loss is { } loss)
                {
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
            Status="训练与独立评估完成，已归档到模型库。";Progress=100;RemainingText="已完成";
            await RefreshModelsAsync();SelectedModel=Models.First(m=>m.Directory==directory);await PersistSessionAsync();
        }
        catch(OperationCanceledException){Status="训练已停止，部分产物保留，未发布为已验证模型。";RemainingText="已停止";await RefreshModelsAsync();}
        catch(Exception error){Status="训练失败："+error.Message;RemainingText="失败";AppendLog(Status);await RefreshModelsAsync();}
        finally{_elapsed.Stop();_clock.Stop();_operation=null;IsWaiting=false;IsBusy=false;}
    }
    public async Task TestAsync(IReadOnlyList<string> files)
    {
        if(!CanTest||files.Count==0)return;
        if(files.Count>100){Status="每批最多选择100张原图。";return;}
        var model=SelectedModel!;using var operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_operation=operation;IsBusy=true;
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
            Status=$"测试结束：成功 {succeeded}，失败 {failed}。";await PersistSessionAsync();
        }
        catch(OperationCanceledException){Status=$"测试已停止：成功 {succeeded}，失败 {failed}。";}
        finally{_operation=null;IsBusy=false;}
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
    public async Task StopAndWaitAsync(){_lifetime.Cancel();_operation?.Cancel();while(IsBusy)await Task.Delay(50);}
    public void Dispose(){_clock.Stop();_lifetime.Cancel();_lifetime.Dispose();}
}
