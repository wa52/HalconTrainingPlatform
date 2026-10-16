using System.IO;
using System.Text.Json;
using System.Windows;
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
            await Step("原路径读取、任务和基础模型自动匹配",async()=>{await vm.SelectDatasetAsync(dataset,CancellationToken.None);Require(vm.CanTrain,vm.Status);Require(vm.TaskLabel is "语义分割" or "图像分类","未识别支持的训练任务");Require(Path.GetFileName(vm.ModelPath)==(vm.IsClassification?"pretrained_dl_classifier_compact.hdl":"pretrained_dl_segmentation_compact.hdl"),"基础模型不匹配");});
            vm.Epochs=args.Contains("--training-epochs")?int.Parse(Option("--training-epochs")):1;
            var before=vm.Models.Select(model=>model.Directory).ToHashSet();
            await Step("真实训练、独立评估、完整归档",async()=>
            {
                var training=vm.TrainAsync();
                while(!training.IsCompleted&&vm.EpochMetrics.Count==0)await Task.Delay(100);
                if(!training.IsCompleted){await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture(window,Path.Combine(output,"wpf-training-live.png"));}
                await training;Require(vm.Progress==100,vm.Status);
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
            vm.SelectedPageIndex=3;await Task.Delay(500);
            var broken=Path.Combine(output,"invalid.png");await File.WriteAllTextAsync(broken,"invalid image fixture");
            await Step("原图批量测试与坏图隔离",async()=>{await vm.TestAsync([images[0],broken,images[1]]);Require(vm.Status.Contains("成功 2，失败 1"),vm.Status);Require(vm.SelectedResult is not null,"没有预测结果");});
            await Step("原生HSmartWindow显示",async()=>{await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(1000);var displays=Descendants<HalconImageView>(window).ToArray();Require(displays.Count(view=>view.IsImageDisplayed)>=(vm.IsClassification?1:2),"原生图像视口未显示："+string.Join(";",displays.Select(d=>d.LastDisplayError)));Capture(window,Path.Combine(output,"wpf-model-test.png"));});
            await Step(vm.IsClassification?"分类得分显示与无掩膜结果持久化":"原尺寸叠加图保存",async()=>{var result=vm.SelectedResult!;if(result.Task=="classification"){Require(!vm.CanSaveOverlay,"分类任务错误启用掩膜保存");Require(result.Scores?.Length==10&&result.ClassName is not null&&result.Confidence is >=0 and <=1,"分类类别或置信度缺失");Require(vm.ClassificationScores.Count==10,"界面分类得分未绑定");return;}await vm.SaveOverlayAsync();var file=Path.Combine(result.OutputDirectory,"overlay.png");Require(File.Exists(file),vm.Status);using var image=new HalconDotNet.HImage(file);image.GetImageSize(out int width,out int height);Require(width==result.Width&&height==result.Height,"叠加图尺寸错误");});
            await Step("桌面连续原图测试",async()=>{for(var i=0;i<5;i++){await vm.TestAsync(images);Require(vm.Status.Contains("成功 2，失败 0"),vm.Status);vm.SelectedPageIndex=1;await Task.Delay(50);vm.SelectedPageIndex=3;await Task.Delay(50);}});
            await Step("训练停止与中断状态",async()=>
            {
                vm.Epochs=60;var old=vm.Models.Select(m=>m.Directory).ToHashSet();var training=vm.TrainAsync();var deadline=DateTime.UtcNow.AddSeconds(20);
                while(!(vm.IsBusy&&vm.Epoch=="—"&&vm.LogText.Contains("加载模型"))&&DateTime.UtcNow<deadline)await Task.Delay(50);
                vm.RequestStop();await training.WaitAsync(TimeSpan.FromSeconds(30));Require(vm.Models.Any(m=>!old.Contains(m.Directory)&&m.Status=="interrupted"),vm.Status);
            });
            await Step("模型与测试历史重载",async()=>{var store=new FileWorkspaceStore(vm.Root);var session=await store.LoadSessionAsync();Require(session?.Dataset==dataset,"数据集会话未保存");var models=await store.ModelsAsync();var model=originalModel is not null?models.First(m=>m.Directory==originalModel.Directory):models.First(m=>m.CanTest);var history=await store.ResultsAsync(model.Directory);Require(history.Count>0,"测试历史没有恢复");});
            vm.SelectedPageIndex=3;if(originalModel is not null)vm.SelectedModel=vm.Models.First(m=>m.Directory==originalModel.Directory);await Task.Delay(1000);Capture(window,Path.Combine(output,"wpf-final.png"));
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
        var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(file);encoder.Save(stream);
    }
}
