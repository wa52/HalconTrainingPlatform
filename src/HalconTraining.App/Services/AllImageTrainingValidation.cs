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

internal static class AllImageTrainingValidation
{
    public static async Task<int> RunAsync(MainWindow window,DesktopWorkspaceViewModel vm,string output,bool manual)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();
        void Require(bool value,string label){if(!value)throw new InvalidOperationException(label);checks.Add(label);}
        async Task Layout(){await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);window.UpdateLayout();}
        try
        {
            Require(Path.GetFullPath(vm.Root).Equals(Path.GetFullPath(Path.Combine(output,"workspace")),StringComparison.OrdinalIgnoreCase),"仅使用隔离工作区");
            var source=Path.Combine(output,"fixture-images");var paths=new List<string>();
            foreach(var folder in new[]{"有目标样本","背景样本"})for(var i=0;i<(folder=="有目标样本"?5:2);i++)
            {
                var file=Path.Combine(source,folder,$"image-{i}.png");Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64,64,96,96,PixelFormats.Bgr32,null,new byte[64*64*4],64*4)));
                using(var stream=File.Create(file))encoder.Save(stream);paths.Add(file);
            }
            await vm.ActiveLearning.ImportFolderAsync(source);vm.SelectedPageIndex=0;window.DatasetPages.SelectedIndex=1;await Layout();
            var active=vm.ActiveLearning;
            active.SelectedCategory=active.CategoryFilters.Single(c=>c.Name=="有目标样本");
            foreach(var file in paths.Where(p=>Path.GetDirectoryName(p)!.EndsWith("有目标样本")).Take(4))
            {
                active.SelectedRow=active.Items.Single(r=>r.Image.ImagePath==file);
                active.DrawBoxCommand.Execute(new DetectionDrawnBox(2,2,32,32));
                if(file!=paths[3]){active.SaveDraftCommand.Execute(null);while(active.IsBusy||!active.SaveDraftCommand.CanExecute(null))await Task.Delay(20);}
            }
            Require(active.CanCombinedTrain&&active.CanExportHalcon,"未确认有框及无框图片足够时合并训练及本类导出可用");
            await active.ExportHalconDatasetAsync(true);await Layout();
            Require(Directory.Exists(active.LastExport),"通过真实 Worker 导出 HALCON 数据集");
            var filePath=Directory.GetFiles(active.LastExport,"*.hdict").Single();
            using(var report=JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(filePath,"export.json"))))
            {
                var data=report.RootElement;
                Require(data.GetProperty("roundTripVerified").GetBoolean()&&data.GetProperty("images").GetInt32()==7,"HALCON 实际写入复读保留全部 7 张图片");
                Require(data.GetProperty("boxes").GetInt32()==4&&data.GetProperty("negativeImages").GetInt32()==3,"HALCON 保留 4 个未确认框与 3 张空框负样本");
                Require(data.GetProperty("classes").GetArrayLength()==1,"纯背景文件夹不生成目标类别");
            }
            var restored=(await new DetectionActiveLearningStore(vm.Root).LoadLatestAsync())!;
            Require(restored.Images.All(i=>i.Status!="approved")&&restored.Images.Count(i=>i.HasDraft)==4,"训练导出不自动确认标签且保存当前未提交的编辑");
            var view=(DetectionActiveLearningView)((TabItem)window.DatasetPages.Items[1]).Content;
            foreach(var size in new[]{new Size(1380,920),new Size(1120,780)})
            {
                window.Width=size.Width;window.Height=size.Height;await Layout();
                Require(view.CombinedTrainingButton.IsEnabled&&view.DetectionImage.ActualHeight>=180,"两种窗口尺寸保持全量训练入口和图像视口");
            }
            active.SelectedCategory=active.CategoryFilters.Single(c=>c.Name=="背景样本");await Layout();
            Require(active.Items.All(i=>i.Status=="负样本")&&!active.CanBootstrap&&active.CanCombinedTrain,"背景类别自动显示负样本，支持与正样本合并训练");
            await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{passed=true,gpuTrainingExecuted=false,checks,dataset=filePath},new JsonSerializerOptions{WriteIndented=true}));
            if(manual)
            {
                window.Width=1380;window.Height=920;active.ShowThumbnailOverviewCommand.Execute(null);await Layout();
                var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);window.Closed+=(_,_)=>closed.TrySetResult();await closed.Task;
            }
            return 0;
        }
        catch(Exception error){await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString(),checks}));return 1;}
    }
}
