using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HalconTraining.App.ViewModels;
using HalconTraining.App.Views;

namespace HalconTraining.App.Services;

internal static class ImageRemovalValidation
{
    public static async Task<int> RunAsync(MainWindow window,DesktopWorkspaceViewModel vm,string output,bool manual)
    {
        Directory.CreateDirectory(output);
        var checks=new List<string>();
        void Require(bool value,string label){if(!value)throw new InvalidOperationException(label);checks.Add(label);}
        async Task Layout(){await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);window.UpdateLayout();}
        try
        {
            Require(Path.GetFullPath(vm.Root).Equals(Path.GetFullPath(Path.Combine(output,"workspace")),StringComparison.OrdinalIgnoreCase),"仅允许指定证据目录内的隔离工作区");
            var source=Path.Combine(output,"fixture-images");
            var paths=new[]{Path.Combine(source,"验证类别 A","image-1.png"),Path.Combine(source,"验证类别 A","image-2.png"),Path.Combine(source,"验证类别 B","image-3.png")};
            foreach(var path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32,24,96,96,PixelFormats.Bgr32,null,new byte[32*24*4],32*4)));
                using var stream=File.Create(path);encoder.Save(stream);
            }
            var hashes=paths.Select(path=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
            await vm.ActiveLearning.ImportFolderAsync(source);
            vm.SelectedPageIndex=DesktopWorkspaceViewModel.DatasetPage;window.DatasetPages.SelectedIndex=1;await Layout();
            var view=(DetectionActiveLearningView)((TabItem)window.DatasetPages.Items[1]).Content;
            foreach(var size in new[]{new Size(1380,920),new Size(1120,780)})
            {
                window.Width=size.Width;window.Height=size.Height;await Layout();
                Require(view.RemoveDatasetImageButton.IsVisible&&view.RemoveDatasetImageButton.IsEnabled&&view.DetectionImage.ActualHeight>=180,"两种窗口尺寸移除入口可达且保留原图");
                Require(ToolTipService.GetInitialShowDelay(view.RemoveDatasetImageButton)==1500,"移除图像提示延迟 1500 毫秒");
            }
            var selected=vm.ActiveLearning.SelectedRow!.Image.ImagePath;
            var dialog=DetectionSeedDialogs.CreateRemoveImageDialog(selected);
            var cancel=((StackPanel)((StackPanel)dialog.Content).Children[^1]).Children.OfType<Button>().Single(button=>button.IsCancel);
            Require(cancel.IsDefault,"确认窗口默认取消，避免误按回车移除");
            dialog.ContentRendered+=(_,_)=>dialog.Dispatcher.BeginInvoke(new Action(()=>cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Require(dialog.ShowDialog()==false&&vm.ActiveLearning.RootImageCount==3,"取消确认不改变数据集");
            await vm.ActiveLearning.RemoveImageFromDatasetAsync(selected);await Layout();
            Require(vm.ActiveLearning.RootImageCount==2&&vm.ActiveLearning.Items.Count==1&&vm.ActiveLearning.SelectedRow?.Image.ImagePath!=selected,"移除后刷新统计并切换相邻图像");
            vm.ActiveLearning.ShowThumbnailOverviewCommand.Execute(null);await Layout();
            Require(view.ThumbnailImages.Items.Count==1,"缩略图同步移除");
            await vm.ActiveLearning.ImportFolderAsync(source);await Layout();
            Require(vm.ActiveLearning.RootImageCount==2&&!vm.ActiveLearning.Items.Any(row=>row.Image.ImagePath==selected),"重新扫描不会恢复移除的图像");
            var restored=(await new DetectionActiveLearningStore(vm.Root).LoadLatestAsync())!;
            Require(restored.Images.Count==2&&restored.RemovedImagePaths.Contains(selected),"已保存的会话可恢复移除记录");
            foreach(var path in vm.ActiveLearning.Items.Select(row=>row.Image.ImagePath).ToArray())await vm.ActiveLearning.RemoveImageFromDatasetAsync(path);
            await Layout();
            Require(vm.ActiveLearning.SelectedRow is null&&vm.ActiveLearning.Boxes.Count==0&&!view.RemoveDatasetImageButton.IsEnabled,"移除本类最后一张后清空选择与标注并禁用入口");
            for(var i=0;i<paths.Length;i++)Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths[i])))==hashes[i],"源图内容保持不变");
            await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{passed=true,gpuTrainingExecuted=false,checks},new JsonSerializerOptions{WriteIndented=true}));
            if(manual)
            {
                window.Width=1380;window.Height=920;await Layout();
                DetectionSeedDialogs.CreateRemoveImageDialog(Path.Combine(source,"验证类别 B","image-3.png")).ShowDialog();
                var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed+=(_,_)=>closed.TrySetResult();await closed.Task;
            }
            return 0;
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString(),checks}));return 1;
        }
    }
}
