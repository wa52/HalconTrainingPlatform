using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HalconTraining.App.ViewModels;
using HalconTraining.App.Views;

namespace HalconTraining.App.Services;

/// <summary>Explicit desktop check. Reads originals; never approves or trains on generated labels.</summary>
internal static class DetectionFolderValidation
{
    public static async Task<int> RunHdictExportAsync(MainWindow window,DetectionActiveLearningViewModel vm,string output)
    {
        Directory.CreateDirectory(output);
        try
        {
            var view=(DetectionActiveLearningView)((TabItem)window.DatasetPages.Items[1]).Content;
            if(!view.ExportHalconDatasetButton.IsEnabled || view.ExportHalconDatasetButton.Command!=vm.ExportAllHalconCommand)
                throw new InvalidOperationException("顶部导出按钮没有绑定到合并已确认数据集。");
            view.ExportHalconDatasetButton.Command.Execute(null);
            while(vm.IsBusy)await Task.Delay(50);
            if(!vm.OperationHeadline.Contains("已导出")||!Directory.EnumerateFiles(vm.LastExport,"*.hdict").Any())
                throw new InvalidOperationException(vm.Status);
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Capture(window,Path.Combine(output,"hdict-export-native.png"));
            window.Width=1120;window.Height=780;
            await Task.Delay(300);
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Capture(window,Path.Combine(output,"hdict-export-minimum.png"));
            if(!view.CombinedTrainingButton.IsVisible || !view.DetectionRateInput.IsVisible || view.DetectionImage.ActualHeight<180)
                throw new InvalidOperationException($"最小窗口布局不足：图像高度 {view.DetectionImage.ActualHeight:F0}，合并按钮 {view.CombinedTrainingButton.IsVisible}，学习率 {view.DetectionRateInput.IsVisible}。");
            await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{passed=true,vm.LastExport,vm.CombinedDatasetSummary,vm.Status}));
            return 0;
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString(),vm.Status}));
            return 1;
        }
    }

    public static async Task TrainReviewedHalconAsync(MainWindow window, DetectionActiveLearningViewModel vm, string output)
    {
        Directory.CreateDirectory(output);
        vm.TrainingEngine="HALCON · 检测";
        vm.BootstrapEpochs=5;vm.BootstrapBatch=2;vm.BootstrapSize=256;vm.BootstrapRate=.0001;
        var running=vm.TrainCombinedCategoriesAsync();
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Capture(window,Path.Combine(output,"halcon-training-live.png"));
        await running;
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Capture(window,Path.Combine(output,"halcon-training-completed.png"));
        await File.WriteAllTextAsync(Path.Combine(output,"training-results.json"),JsonSerializer.Serialize(new {
            passed=vm.OperationHeadline.StartsWith("训练完成"),engine="HALCON 24.11",mode="combined_approved_only",
            epochs=5,batch=2,imageSize=256,learningRate=.0001,vm.Status,vm.LastExport
        },new JsonSerializerOptions{WriteIndented=true}));
    }

    public static async Task<int> RunParallelCancellationAsync(MainWindow window, DetectionActiveLearningViewModel vm, string output)
    {
        Directory.CreateDirectory(output);
        try
        {
            var ids = vm.CategoryFilters.Where(category=>category.Id.HasValue && category.Approved>=5).Take(3).Select(category=>category.Id!.Value).ToArray();
            if(ids.Length!=3)throw new InvalidOperationException("取消验收需要三个独立人工标注类别。");
            vm.BootstrapEpochs=1000;vm.BootstrapBatch=2;vm.BootstrapSize=256;vm.BootstrapRate=.001;
            var running=vm.TrainCategoriesAsync(ids,2);
            var deadline=DateTimeOffset.UtcNow.AddSeconds(60);
            while(!running.IsCompleted && vm.ParallelJobs.Count(job=>job.ProcessId is not null)<2 && DateTimeOffset.UtcNow<deadline)await Task.Delay(100);
            if(vm.ParallelJobs.Count(job=>job.ProcessId is not null)!=2)throw new InvalidOperationException("未启动两个独立Worker。");
            var pids=vm.ParallelJobs.Where(job=>job.ProcessId is not null).Select(job=>job.ProcessId!.Value).ToArray();
            vm.StopOperationCommand.Execute(null);await running;
            if(vm.ParallelJobs.Count!=3 || vm.ParallelJobs.Any(job=>job.State!="已停止") || vm.ParallelJobs[2].StartedAt is not null)
                throw new InvalidOperationException("停止没有同时取消执行中与排队中的类别。");
            foreach(var pid in pids)
            {
                try {using var process=System.Diagnostics.Process.GetProcessById(pid);if(!process.HasExited)throw new InvalidOperationException("Worker未退出："+pid);}
                catch(ArgumentException) { }
            }
            Capture(window,Path.Combine(output,"parallel-cancelled.png"));
            await File.WriteAllTextAsync(Path.Combine(output,"parallel-cancellation.json"),JsonSerializer.Serialize(new {status="passed",workerProcessesExited=pids,queuedJobNeverStarted=true,vm.OperationDetail,jobs=vm.ParallelJobs.Select(job=>new{job.ClassName,job.State,job.StartedAt,job.ProcessId})},new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception error)
        {
            await vm.StopAndWaitAsync();
            await File.WriteAllTextAsync(Path.Combine(output,"parallel-cancellation.json"),JsonSerializer.Serialize(new {status="failed",error=error.ToString(),vm.Status}));
            return 1;
        }
    }

    public static async Task<int> RunParallelTrainingAsync(MainWindow window, DetectionActiveLearningViewModel vm, string output)
    {
        Directory.CreateDirectory(output);
        try
        {
            var ids = vm.CategoryFilters.Where(category => category.Id.HasValue && category.Approved >= 5).Take(2).Select(category => category.Id!.Value).ToArray();
            if (ids.Length != 2) throw new InvalidOperationException("隔离会话需要两个有人工标注的类别。");
            vm.BootstrapEpochs = 2; vm.BootstrapBatch = 2; vm.BootstrapSize = 256; vm.BootstrapRate = .001;
            var running = vm.TrainCategoriesAsync(ids,2);
            var captured = false;
            while (!running.IsCompleted)
            {
                if (!captured && vm.ParallelJobs.Count == 2 && vm.ParallelJobs.All(job => job.ProcessId is not null && !job.IsFinished))
                { Capture(window, Path.Combine(output,"parallel-live.png")); captured = true; }
                await Task.Delay(100);
            }
            await running;
            if (vm.ParallelJobs.Count != 2 || vm.ParallelJobs.Any(job => job.State != "已完成" || job.Epoch != 2 || job.Loss is null))
                throw new InvalidOperationException("两份真实训练没有全部完成："+vm.Status);
            if (!captured || vm.ParallelJobs.Select(job => job.ProcessId).Distinct().Count()!=2 ||
                vm.ParallelJobs.Max(job=>job.ProcessReportedAt) >= vm.ParallelJobs.Min(job=>job.EndedAt))
                throw new InvalidOperationException("没有证据表明两个独立 Worker 同时存活。");
            foreach (var job in vm.ParallelJobs)
            {
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job.OutputDirectory,"manifest.json")));
                var classes = manifest.RootElement.GetProperty("classIds");
                if (classes.GetArrayLength()!=1 || classes[0].GetInt32()!=job.ClassId || !File.Exists(Path.Combine(job.OutputDirectory,"model.pt")))
                    throw new InvalidOperationException("类别模型混用或模型未保存。");
            }
            Capture(window,Path.Combine(output,"parallel-completed.png"));
            await File.WriteAllTextAsync(Path.Combine(output,"parallel-validation.json"),JsonSerializer.Serialize(new { status="passed",simultaneousWorkers=true,vm.OperationHeadline,vm.OperationDetail,jobs=vm.ParallelJobs.Select(job=>new {job.ClassId,job.ClassName,job.State,job.Epoch,job.Loss,job.ProcessId,job.ProcessReportedAt,job.StartedAt,job.EndedAt,job.OutputDirectory}) },new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output,"parallel-validation.json"),JsonSerializer.Serialize(new {status="failed",error=error.ToString(),vm.Status}));
            Capture(window,Path.Combine(output,"parallel-failed.png")); return 1;
        }
    }

    public static async Task<int> RunTrainingFeedbackAsync(MainWindow window, DetectionActiveLearningViewModel vm, string output)
    {
        Directory.CreateDirectory(output);
        var headlines = new List<string>(); var details = new List<string>();
        void CaptureProgress(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(vm.OperationHeadline)) headlines.Add(vm.OperationHeadline);
            if (args.PropertyName == nameof(vm.OperationDetail)) details.Add(vm.OperationDetail);
        }
        vm.PropertyChanged += CaptureProgress;
        try
        {
            vm.SelectedCategory = vm.CategoryFilters.First(filter => filter.Approved >= 5);
            vm.BootstrapEpochs = 2; vm.BootstrapBatch = 3; vm.BootstrapSize = 256; vm.BootstrapRate = .001;
            if (!vm.CanBootstrap) throw new InvalidOperationException(vm.TrainingEligibility);
            var training = vm.TrainCurrentCategoryAsync();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!vm.IsTrainingOperation || vm.TrainButtonLabel != "训练中…") throw new InvalidOperationException("点击训练后没有立即显示运行状态。");
            while (!training.IsCompleted && vm.OperationHeadline.StartsWith("准备", StringComparison.Ordinal)) await Task.Delay(100);
            Capture(window, Path.Combine(output, "training-live.png"));
            await training;
            if (!vm.OperationHeadline.StartsWith("训练完成", StringComparison.Ordinal) || !vm.HasTrainedModel || vm.OperationProgress != 100)
                throw new InvalidOperationException(vm.Status);
            if (!headlines.Any(line => line.Contains("Epoch 1/2")) || !details.Any(line => line.Contains("Loss")))
                throw new InvalidOperationException("真实训练轮次或Loss未到达界面。");
            Capture(window, Path.Combine(output, "training-completed.png"));
            var expectedPredictions = vm.RemainingPredictionCount;
            await vm.PredictRemainingAsync();
            if (vm.OperationHeadline != "预测完成" || !headlines.Any(line => line.StartsWith("预测中", StringComparison.Ordinal)))
                throw new InvalidOperationException("真实预测进度未到达界面：" + vm.Status);
            Capture(window, Path.Combine(output, "prediction-completed.png"));
            await File.WriteAllTextAsync(Path.Combine(output, "feedback-validation.json"), JsonSerializer.Serialize(new
            { status = "passed", expectedPredictions, headlines, details, vm.OperationHeadline, vm.OperationDetail, vm.OperationProgress, vm.OperationElapsedText }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "feedback-validation.json"), JsonSerializer.Serialize(new
            { status = "failed", error = error.ToString(), headlines, details, workspaceStatus = vm.Status, vm.OperationLog }, new JsonSerializerOptions { WriteIndented = true }));
            Capture(window, Path.Combine(output, "feedback-failed.png")); return 1;
        }
        finally { vm.PropertyChanged -= CaptureProgress; }
    }

    public static async Task<int> RunAsync(MainWindow window, DetectionActiveLearningViewModel vm, string folder, string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Require(bool passed, string label) { if (!passed) throw new InvalidOperationException(label); checks.Add(label); }
        var view = (DetectionActiveLearningView)((TabItem)window.DatasetPages.Items[1]).Content;
        try
        {
            await vm.ImportFolderAsync(folder);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(vm.HasSession && vm.DatasetImageCount > 0, "真实文件夹加载");
            Require(vm.CategoryFilters.Count == Directory.GetDirectories(folder).Length, "子文件夹自动识别为类别");
            Require(vm.RemainingPredictionCount == vm.DatasetImageCount && !vm.CanBootstrap, "图片未自动标注或确认");
            foreach (var category in vm.CategoryFilters.Where(filter => filter.Id.HasValue).ToArray())
            {
                vm.SelectedCategory = category;
                Require(vm.Categories.Count == 1 && vm.DatasetImageCount == category.Count && vm.Items.Count == category.Count && vm.Items.All(row => row.Image.FolderClassId == category.Id), "类别筛选：" + category.Name);
                Require(vm.DrawClassId == category.Id, "画框默认类别：" + category.Name);
            }
            var first = vm.CategoryFilters.First(filter => filter.Id.HasValue && filter.Count > 0);
            vm.SelectedCategory = first;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(400);
            Require(view.DetectionImage.IsImageDisplayed && view.DetectionImage.ActualHeight > 220, "原生原图大视口显示");
            var image = vm.SelectedRow!.Image;
            vm.DrawBoxCommand.Execute(new DetectionDrawnBox(image.Width * .2, image.Height * .2, image.Width * .8, image.Height * .8));
            Require(vm.Boxes.Count == 1 && vm.Boxes[0].ClassId == first.Id, "原图框选使用目录类别（仅验证，不确认）");
            Require(image.ReviewedBoxes.Count == 0 && image.Status == "pending", "框选测试未生成已确认训练标签");
            vm.Boxes.Clear();
            vm.SelectedRow = vm.Items.Last();
            view.DetectionRateInput.SetCurrentValue(TextBox.TextProperty, "abc");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(vm.HasParameterErrors && !view.TrainDetectionButton.IsEnabled, "错误参数阻止训练");
            view.DetectionRateInput.SetCurrentValue(TextBox.TextProperty, "0.001");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(!vm.HasParameterErrors, "修正参数后恢复正常状态");
            await vm.PredictRemainingAsync();
            Require(vm.Status.Contains("请先确认") && !vm.IsBusy, "无初始模型时不虚构预测");
            vm.SelectedCategory = vm.CategoryFilters.First(filter => filter.Id == first.Id);
            vm.SelectedRow = vm.Items.First();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(output, "folder-detection.png"));
            window.Width = 1120; window.Height = 780;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(output, "folder-detection-minimum.png"));
            Require(view.DetectionImage.ActualHeight >= 200 && view.DetectionRateInput.IsVisible, "最小窗口保持原图与核心参数可见");
            await vm.InitializeAsync();
            Require(vm.DatasetImageCount == vm.RemainingPredictionCount && !vm.CanBootstrap, "会话重载保留目录类别且无测试标签污染");
            await File.WriteAllTextAsync(Path.Combine(output, "validation.json"), JsonSerializer.Serialize(new { status = "passed", checks, vm.DatasetImageCount, vm.RootImageCount, categories = vm.CategoryFilters }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "validation.json"), JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString(), workspaceStatus = vm.Status }, new JsonSerializerOptions { WriteIndented = true }));
            Capture(window, Path.Combine(output, "failed.png"));
            return 1;
        }
    }

    private static void Capture(MainWindow window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
