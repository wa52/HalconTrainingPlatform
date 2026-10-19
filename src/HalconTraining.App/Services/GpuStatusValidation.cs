using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HalconTraining.App.ViewModels;

namespace HalconTraining.App.Services;

internal static class GpuStatusValidation
{
    public static async Task<int> RunAsync(MainWindow window,DesktopWorkspaceViewModel vm,string output)
    {
        Directory.CreateDirectory(output);
        try
        {
            var deadline=DateTimeOffset.UtcNow.AddSeconds(12);
            while(vm.Gpu.SampleCount<2&&DateTimeOffset.UtcNow<deadline)await Task.Delay(100);
            if(vm.Gpu.SampleCount<2||!vm.Gpu.IsAvailable)throw new InvalidOperationException(vm.Gpu.Details);
            var panels=new List<object>();
            foreach(var page in new[]{DesktopWorkspaceViewModel.DatasetPage,DesktopWorkspaceViewModel.TrainingPage,DesktopWorkspaceViewModel.ModelsPage})
            {
                vm.SelectedPageIndex=page;
                await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                if(!window.GpuStatusPanel.IsVisible)throw new InvalidOperationException("GPU状态区在页面切换后消失。");
                Capture(window,Path.Combine(output,$"page-{page}.png"));
                panels.Add(new{page,vm.Gpu.Summary,vm.Gpu.Memory,vm.Gpu.SampleCount});
            }
            window.Width=1120;window.Height=780;
            await Task.Delay(300);window.UpdateLayout();
            var root=(FrameworkElement)window.Content;
            var origin=window.GpuStatusPanel.TranslatePoint(new Point(0,0),root);
            if(origin.X<0||origin.X+window.GpuStatusPanel.ActualWidth>root.ActualWidth||window.GpuStatusPanel.ActualHeight>64)
                throw new InvalidOperationException("最小窗口GPU状态栏超出可见范围。");
            Capture(window,Path.Combine(output,"minimum.png"));
            await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{passed=true,panels,vm.Gpu.Details}));
            return 0;
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString()}));
            return 1;
        }
    }

    private static void Capture(MainWindow window,string path)
    {
        var root=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(path);encoder.Save(file);
    }
}
