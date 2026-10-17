using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HalconTraining.Application.Workspace;
using HalconTraining.App.ViewModels;
using HalconTraining.App.Views;
using HalconTraining.Persistence.Workspace;

namespace HalconTraining.App.Services;

/// <summary>Optional real desktop integration check; never runs during normal startup.</summary>
internal static class DesktopValidation
{
    public static async Task<int> RunAsync(MainWindow window,DesktopWorkspaceViewModel vm,string[] args)
    {
        string Option(string name)=>args[Array.IndexOf(args,name)+1];
        var output=Option("--validation-output");Directory.CreateDirectory(output);
        var steps=new List<object>();
        void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        async Task Step(string name,Func<Task> action){await action();steps.Add(new {name,status="passed",at=DateTimeOffset.Now});await File.WriteAllTextAsync(Path.Combine(output,"desktop-validation.json"),JsonSerializer.Serialize(steps,WorkspaceJson.Options));}
        try
        {
            var dataset=Option("--dataset");var images=args.Select((arg,i)=>(arg,i)).Where(pair=>pair.arg=="--image").Select(pair=>args[pair.i+1]).ToArray();
            Require(images.Length>=2,"需要两张真实原图。");
            var originalModel=vm.Models.FirstOrDefault(model=>model.CanTest);
            await Step("三页导航、空状态与低频功能默认折叠",async()=>
            {
                Require(window.Pages.Items.Count==3,"必须只有三个主页面");
                Require(window.Pages.Items.Cast<TabItem>().Select(item=>item.Header?.ToString()).SequenceEqual(new[]{"数据集","训练","模型"}),"主页面命名不符");
                Require(!vm.CanTrain&&vm.TrainingReadiness.Contains("选择"),"空状态应说明选择数据集");
                vm.SelectedPageIndex=DesktopWorkspaceViewModel.TrainingPage;
                await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                Require(!window.StartTrainingButton.IsEnabled,"空状态不能开始训练");
                Require(Descendants<Expander>(window).Where(e=>e.Header?.ToString() is "高级参数" or "详细训练指标" or "运行日志").All(e=>!e.IsExpanded),"低频区域应默认折叠");
                window.Width=1120;window.Height=780;await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture(window,Path.Combine(output,"wpf-empty-minimum.png"));
                window.Width=1380;window.Height=920;vm.SelectedPageIndex=DesktopWorkspaceViewModel.DatasetPage;
            });
            await Step("原路径读取、任务和基础模型自动匹配",async()=>{await vm.SelectDatasetAsync(dataset,CancellationToken.None);Require(vm.CanTrain,vm.Status);Require(vm.TaskLabel is "语义分割" or "图像分类","未识别支持的训练任务");Require(Path.GetFileName(vm.ModelPath)==(vm.IsClassification?"pretrained_dl_classifier_compact.hdl":"pretrained_dl_segmentation_compact.hdl"),"基础模型不匹配");});
            await Step("全部图片、类别标注与下一步入口",async()=>
            {
                Require(vm.Samples.Count==(vm.IsClassification?1602:332),"图片浏览不能截断到前100张");
                Require(vm.Classes.Count==(vm.IsClassification?10:2),"类别未显示");
                if(vm.IsClassification)Require(!string.IsNullOrEmpty(vm.SampleLabel),"分类标注未显示");
                else Require(File.Exists(vm.PreviewMaskPath),"分割标注未显示");
                await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                Require(window.DatasetImage.IsImageDisplayed,"数据集原图未显示");
                Require(window.DatasetImage.ActualWidth>window.ActualWidth/2,"数据集图像应占主要区域");
                vm.NextSampleCommand.Execute(null);Require(vm.CanPreviousSample,"下一张不可用");vm.PreviousSampleCommand.Execute(null);
                Capture(window,Path.Combine(output,"wpf-dataset.png"));
                vm.GoTrainingCommand.Execute(null);Require(vm.SelectedPageIndex==DesktopWorkspaceViewModel.TrainingPage,"下一步未进入训练页");
            });
            await Step("四项核心参数常显可编辑、划分真实更新、任务信息匹配",async()=>
            {
                Require(!window.AdvancedParameters.IsExpanded&&window.CoreParameters.IsVisible,"核心参数不能依赖高级展开");
                Require(window.LearningRateInput.IsVisible&&window.LearningRateInput.IsEnabled,"学习率必须直接可修改");
                var rate=vm.LearningRate;
                window.LearningRateInput.SetCurrentValue(TextBox.TextProperty,"0.0007");await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                Require(Math.Abs(vm.LearningRate-.0007)<1e-12,"学习率修改未传入训练配置");
                window.LearningRateInput.SetCurrentValue(TextBox.TextProperty,"abc");await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                Require(vm.HasInputErrors&&!vm.CanTrain,"错误学习率未阻止训练");
                window.LearningRateInput.SetCurrentValue(TextBox.TextProperty,rate.ToString(System.Globalization.CultureInfo.InvariantCulture));await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                Require(!vm.HasInputErrors&&vm.CanTrain,"学习率修正后未恢复");
                Require(vm.SplitRatioSummary=="70% / 15% / 15%"&&vm.TrainCountText==(vm.IsClassification?"1,121":"232"),"默认比例和真实划分数量不匹配");
                vm.TrainPercent=60;Require(vm.HasPendingSplit&&!vm.CanTrain&&vm.TrainCountText=="待更新","比例修改后不能继续展示旧数量");
                await vm.SelectDatasetAsync(dataset,CancellationToken.None);Require(!vm.HasPendingSplit&&vm.CanTrain,"更新划分未恢复训练");
                Require(vm.TrainCountText==(vm.IsClassification?"961":"199"),"更新后数量并非真实划分");
                vm.TrainPercent=70;await vm.SelectDatasetAsync(dataset,CancellationToken.None);
                Require(vm.TaskParameterHeader.Contains(vm.IsClassification?"分类":"分割")&&vm.TaskParameterSummary.Contains(vm.IsClassification?"准确率":"mIoU"),"任务信息不匹配");
                window.Width=1120;window.Height=780;await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                var parameterViewport=Descendants<ScrollViewer>(window).First(view=>view.IsAncestorOf(window.LearningRateInput));
                Require(window.LearningRateInput.TranslatePoint(new Point(),parameterViewport).Y+window.LearningRateInput.ActualHeight<parameterViewport.ActualHeight-2,"最小窗口不能隐藏学习率");
                Require(window.BaseModelSummary.TranslatePoint(new Point(),parameterViewport).Y+window.BaseModelSummary.ActualHeight<parameterViewport.ActualHeight-2,"最小窗口不能隐藏基础模型信息");
                Capture(window,Path.Combine(output,"wpf-training-core-minimum.png"));
                window.Width=1380;window.Height=920;await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                Capture(window,Path.Combine(output,"wpf-training-core.png"));
            });
            await Step("参数无效时说明原因并阻止训练",async()=>
            {
                vm.Epochs=0;await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                Require(!vm.CanTrain&&!window.StartTrainingButton.IsEnabled&&vm.TrainingReadiness.Contains("大于"),"零轮次未阻止训练");
                vm.Epochs=1;
                window.EpochInput.SetCurrentValue(TextBox.TextProperty,"abc");await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                Require(vm.HasInputErrors&&!vm.CanTrain&&vm.TrainingReadiness.Contains("格式"),"文本参数错误未阻止训练");
                Capture(window,Path.Combine(output,"wpf-parameter-error.png"));
                window.EpochInput.SetCurrentValue(TextBox.TextProperty,"1");await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                Require(!vm.HasInputErrors&&vm.CanTrain,"修正输入后未恢复训练");
                Require(window.EpochInput.FontWeight==FontWeights.Normal,"页签强调样式不能污染正文和参数字号");
                Capture(window,Path.Combine(output,"wpf-training-ready.png"));
            });
            if(args.Contains("--layout-only"))
            {
                await Step("现有模型原图测试与最小窗口布局",async()=>
                {
                    Require(originalModel is not null,"布局验证需要已验证模型副本");
                    vm.SelectedModel=originalModel;vm.GoModelsCommand.Execute(null);
                    await Task.Delay(200);await vm.TestAsync(images);Require(vm.Status.Contains("成功 2，失败 0"),vm.Status);
                    await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(500);
                    Require(Descendants<HalconImageView>(window).Count(v=>v.IsImageDisplayed)>=(vm.IsClassification?1:2),"原生测试图像未显示");
                    Capture(window,Path.Combine(output,"wpf-model-test.png"));
                    window.Width=1120;window.Height=780;await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture(window,Path.Combine(output,"wpf-model-minimum.png"));
                });
                if(!args.Contains("--no-training"))await Step("停止训练后保留监控与明确状态",async()=>
                {
                    vm.Epochs=60;var running=vm.TrainAsync();var deadline=DateTime.UtcNow.AddSeconds(20);
                    while(!vm.IsTraining&&!running.IsCompleted&&DateTime.UtcNow<deadline)await Task.Delay(50);
                    Require(vm.IsTraining&&!vm.AreParametersExpanded&&vm.CanStop,"开始训练未切换焦点");
                    vm.RequestStop();await running.WaitAsync(TimeSpan.FromSeconds(30));
                    Require(!vm.IsTraining&&vm.ShowTrainingMonitor&&vm.TrainingStateText=="已停止","停止后状态不明确或日志被隐藏");
                });
                return 0;
            }
            vm.Epochs=args.Contains("--training-epochs")?int.Parse(Option("--training-epochs")):1;
            var before=vm.Models.Select(model=>model.Directory).ToHashSet();
            await Step("真实训练、独立评估、完整归档",async()=>
            {
                var training=vm.TrainAsync();
                while(!training.IsCompleted&&!vm.IsTraining)await Task.Delay(50);
                if(!training.IsCompleted){Require(vm.ShowTrainingMonitor&&!vm.AreParametersExpanded,"训练中应收起参数并聚焦监控");Require(vm.SelectedPageIndex==DesktopWorkspaceViewModel.TrainingPage,"训练导航错误");}
                while(!training.IsCompleted&&vm.TrainingCurve.Count<10)await Task.Delay(100);
                if(!training.IsCompleted){await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture(window,Path.Combine(output,"wpf-training-live.png"));}
                await training;Require(vm.Progress==100&&vm.HasCompletedTraining&&!vm.IsTraining,vm.Status);
                Require(vm.TrainingCurve.Count>0&&vm.ValidationCurve.Count>0,"缺少真实训练或验证曲线");
                Require(vm.EpochMetrics.Count==vm.Epochs,"每轮记录不完整");
                await File.WriteAllTextAsync(Path.Combine(output,"training-monitor.json"),JsonSerializer.Serialize(new {vm.EpochMetrics,vm.TrainingCurve,vm.ValidationCurve,vm.ValidationIoU,vm.BestValidationIoU,vm.ElapsedText},WorkspaceJson.Options));
                Capture(window,Path.Combine(output,"wpf-training-completed.png"));
                var model=vm.Models.FirstOrDefault(model=>!before.Contains(model.Directory)&&model.CanTest);Require(model is not null,vm.Status);
                foreach(var file in new[]{"model.hdl","preprocess.hdict","manifest.json","classes.json","training_params.json","split.json","validation_report.json","checksums.json","training.log","job.json"})Require(File.Exists(Path.Combine(model!.Directory,file)),"归档缺少"+file);
                using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(model!.Directory,"manifest.json")));Require(manifest.RootElement.GetProperty("status").GetString()=="reload_and_evaluation_passed","评估状态未闭环");
                await File.WriteAllTextAsync(Path.Combine(output,"trained-model.txt"),model.Directory);
            });
            await Step("完成后计时冻结",async()=>{var elapsed=vm.ElapsedText;await Task.Delay(1200);Require(vm.ElapsedText==elapsed,"完成后耗时仍变化");});
            if(originalModel is not null)vm.SelectedModel=vm.Models.First(model=>model.Directory==originalModel.Directory);
            vm.GoModelsCommand.Execute(null);Require(vm.SelectedPageIndex==DesktopWorkspaceViewModel.ModelsPage,"查看模型入口未切换");await Task.Delay(500);
            var broken=Path.Combine(output,"invalid.png");await File.WriteAllTextAsync(broken,"invalid image fixture");
            await Step("原图批量测试与坏图隔离",async()=>{await vm.TestAsync([images[0],broken,images[1]]);Require(vm.Status.Contains("成功 2，失败 1"),vm.Status);Require(vm.SelectedResult is not null,"没有预测结果");});
            await Step("原生HSmartWindow显示",async()=>{await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(1000);var displays=Descendants<HalconImageView>(window).ToArray();Require(displays.Count(view=>view.IsImageDisplayed)>=(vm.IsClassification?1:2),"原生图像视口未显示："+string.Join(";",displays.Select(d=>d.LastDisplayError)));Capture(window,Path.Combine(output,"wpf-model-test.png"));});
            await Step(vm.IsClassification?"分类得分显示与无掩膜结果持久化":"原尺寸叠加图保存",async()=>{var result=vm.SelectedResult!;if(result.Task=="classification"){Require(!vm.CanSaveOverlay,"分类任务错误启用掩膜保存");Require(result.Scores?.Length==10&&result.ClassName is not null&&result.Confidence is >=0 and <=1,"分类类别或置信度缺失");Require(vm.ClassificationScores.Count==10,"界面分类得分未绑定");return;}await vm.SaveOverlayAsync();var file=Path.Combine(result.OutputDirectory,"overlay.png");Require(File.Exists(file),vm.Status);using var image=new HalconDotNet.HImage(file);image.GetImageSize(out int width,out int height);Require(width==result.Width&&height==result.Height,"叠加图尺寸错误");});
            await Step("桌面连续原图测试",async()=>{for(var i=0;i<5;i++){await vm.TestAsync(images);Require(vm.Status.Contains("成功 2，失败 0"),vm.Status);vm.SelectedPageIndex=DesktopWorkspaceViewModel.DatasetPage;await Task.Delay(50);vm.SelectedPageIndex=DesktopWorkspaceViewModel.ModelsPage;await Task.Delay(50);}});
            await Step("训练停止与中断状态",async()=>
            {
                vm.Epochs=60;var old=vm.Models.Select(m=>m.Directory).ToHashSet();var training=vm.TrainAsync();var deadline=DateTime.UtcNow.AddSeconds(20);
                while(!(vm.IsBusy&&vm.Epoch=="—"&&vm.LogText.Contains("加载模型"))&&DateTime.UtcNow<deadline)await Task.Delay(50);
                vm.RequestStop();await training.WaitAsync(TimeSpan.FromSeconds(30));Require(vm.Models.Any(m=>!old.Contains(m.Directory)&&m.Status=="interrupted"),vm.Status);
            });
            await Step("模型与测试历史重载",async()=>{var store=new FileWorkspaceStore(vm.Root);var session=await store.LoadSessionAsync();Require(session?.Dataset==dataset,"数据集会话未保存");var models=await store.ModelsAsync();var model=originalModel is not null?models.First(m=>m.Directory==originalModel.Directory):models.First(m=>m.CanTest);var history=await store.ResultsAsync(model.Directory);Require(history.Count>0,"测试历史没有恢复");});
            vm.SelectedPageIndex=DesktopWorkspaceViewModel.ModelsPage;if(originalModel is not null)vm.SelectedModel=vm.Models.First(m=>m.Directory==originalModel.Directory);await Task.Delay(1000);Capture(window,Path.Combine(output,"wpf-final.png"));
            return 0;
        }
        catch(Exception error){steps.Add(new {status="failed",error=error.ToString(),workspaceStatus=vm.Status});await File.WriteAllTextAsync(Path.Combine(output,"desktop-validation.json"),JsonSerializer.Serialize(steps,WorkspaceJson.Options));Capture(window,Path.Combine(output,"wpf-failed.png"));return 1;}
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var nested in Descendants<T>(child))yield return nested;}
    }
    private static void Capture(Window window,string file)
    {
        var root=(FrameworkElement)window.Content;var image=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(file);encoder.Save(stream);
    }
}
