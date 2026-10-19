using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HalconTraining.App.ViewModels;
using HalconTraining.App.Views;

namespace HalconTraining.App.Services;

/// <summary>Explicit presentation checks in a separate workspace; never starts training.</summary>
internal static class UiPolicyValidation
{
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        var seen=new HashSet<DependencyObject>();var pending=new Stack<DependencyObject>();pending.Push(root);
        while(pending.TryPop(out var item))
        {
            if(!seen.Add(item))continue;
            yield return item;
            if(item is Visual)for(var i=0;i<VisualTreeHelper.GetChildrenCount(item);i++)pending.Push(VisualTreeHelper.GetChild(item,i));
            foreach(var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())pending.Push(child);
        }
    }
    private static void Capture(Window window,string file)
    {
        var root=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(file);encoder.Save(stream);
    }
    public static async Task<int> RunAsync(MainWindow window,DesktopWorkspaceViewModel vm,string output,bool manual)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();var controlCounts=new List<object>();
        void Require(bool success,string label){if(!success)throw new InvalidOperationException(label);checks.Add(label);}
        async Task Layout(){await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);window.UpdateLayout();}
        void Audit(Window current)
        {
            var controls=Walk(current).OfType<FrameworkElement>().Where(element=>element.IsLoaded).ToArray();
            var actions=controls.Where(element=>ActionVisual.GetKind(element) is not null).ToArray();
            Require(actions.Length>0,"存在操作图标："+current.Title);
            foreach(var action in actions)
            {
                if(!ActionIcon.Exists(ActionVisual.GetKind(action)!))throw new InvalidOperationException("未知图标");
                if(string.IsNullOrWhiteSpace(AutomationProperties.GetName(action)))throw new InvalidOperationException("操作缺少可访问名称："+action.GetType().Name);
                if(ToolTipService.GetInitialShowDelay(action)!=1500||ToolTipService.GetBetweenShowDelay(action)!=0||!ToolTipService.GetShowOnDisabled(action))
                    throw new InvalidOperationException("提示时序或禁用提示错误："+AutomationProperties.GetName(action));
            }
            foreach(var button in controls.OfType<Button>().Where(button=>!ActionVisual.GetDataItem(button)&&!(button.Content is null&&button.TemplatedParent is not null)))
                if(ActionVisual.GetKind(button) is null)throw new InvalidOperationException("文字操作未图标化："+button.Content);
            foreach(var button in controls.OfType<Button>().Where(button=>button.IsVisible&&button.Parent is Panel))
            {
                var parent=(Panel)button.Parent;
                var p=button.TranslatePoint(new Point(),parent);
                if(p.X<-.5||p.Y<-.5||p.X+button.ActualWidth>parent.ActualWidth+.5||p.Y+button.ActualHeight>parent.ActualHeight+.5)
                    throw new InvalidOperationException("按钮越出布局："+AutomationProperties.GetName(button));
            }
            controlCounts.Add(new{current.Title,actions=actions.Length,width=current.ActualWidth,height=current.ActualHeight});
        }
        try
        {
            Require(window.Title=="HALCON 训练平台","软件名称保持稳定");
            Require(window.Pages.Items.Cast<TabItem>().All(tab=>tab.HeaderTemplate==(DataTemplate)window.FindResource("IconTabTemplate")),"三页导航使用图标与可见标题");
            foreach(var size in new[]{new Size(1380,920),new Size(1120,780)})
            {
                window.Width=size.Width;window.Height=size.Height;
                window.DatasetPages.SelectedIndex=0;
                for(var page=0;page<3;page++)
                {
                    vm.SelectedPageIndex=page;await Layout();Audit(window);
                    if(page==DesktopWorkspaceViewModel.DatasetPage&&!vm.HasDataset)
                        Require(window.DatasetEmptyState.IsVisible&&window.DatasetEmptyState.ActualHeight<260,"数据集空状态只显示紧凑操作指引");
                    if(page==DesktopWorkspaceViewModel.ModelsPage&&vm.SelectedResult is null)
                        Require(window.ModelEmptyState.IsVisible&&window.ModelEmptyState.ActualHeight<280,"模型空状态显示测试指引而非空图像区");
                    if(page==DesktopWorkspaceViewModel.TrainingPage)
                    {
                        var monitor=Walk(window).OfType<TrainingMonitorView>().Single();
                        Require(monitor.EmptyMonitor.IsVisible&&monitor.MonitorPanel.ActualHeight<360,"空闲监控按内容收缩且给出下一步");
                        Require(window.StartTrainingButton.ContentTemplate==(DataTemplate)window.FindResource("IconTextActionTemplate"),"主要训练操作保留可见文字");
                        var oldRate=window.LearningRateInput.Text;
                        window.LearningRateInput.Text="无效参数";await Layout();
                        Require(Validation.GetHasError(window.LearningRateInput)&&vm.HasInputErrors&&!vm.CanTrain,"错误参数显示校验反馈并阻止训练");
                        window.LearningRateInput.Text=oldRate;await Layout();
                        Require(!Validation.GetHasError(window.LearningRateInput)&&!vm.HasInputErrors,"修改参数后恢复编辑校验状态");
                        // Presentation-state fixtures only: no worker, metrics or training results are fabricated.
                        typeof(DesktopWorkspaceViewModel).GetProperty(nameof(vm.IsTraining))!.SetValue(vm,true);await Layout();
                        Require(!window.CoreParameters.IsVisible&&window.StopTrainingButton.IsVisible&&!monitor.EmptyMonitor.IsVisible,"运行状态锁定参数且显示停止与监控");
                        typeof(DesktopWorkspaceViewModel).GetProperty(nameof(vm.IsTraining))!.SetValue(vm,false);await Layout();
                        Require(window.CoreParameters.IsVisible&&window.CoreParameters.IsEnabled&&monitor.EmptyMonitor.IsVisible,"退出运行状态恢复核心参数与空闲监控");
                    }
                    Capture(window,Path.Combine(output,$"page-{page}-{size.Width}.png"));
                }
                vm.SelectedPageIndex=DesktopWorkspaceViewModel.DatasetPage;window.DatasetPages.SelectedIndex=1;await Layout();
                var view=(DetectionActiveLearningView)((TabItem)window.DatasetPages.Items[1]).Content;
                Audit(window);
                Require(view.DetectionImage.ActualHeight>=180,"最小窗口保留原图视口");
                var split=Walk(view).OfType<GridSplitter>().Single();
                var splitGrid=(Grid)split.Parent;
                foreach(var sideWidth in new[]{190,360})
                {
                    splitGrid.ColumnDefinitions[0].Width=new GridLength(sideWidth);await Layout();Audit(window);
                    Require(view.DetectionImage.ActualWidth>=440,"调整分栏后原图保留最小宽度");
                }
                splitGrid.ColumnDefinitions[0].Width=new GridLength(250);await Layout();
                var exportButtons=Walk(view).OfType<Button>().Count(button=>ReferenceEquals(button.Command,vm.ActiveLearning.ExportAllHalconCommand));
                Require(exportButtons==1,"合并 HDICT 导出仅一个入口");
                Require(Walk(window).OfType<Button>().Count(button=>ReferenceEquals(button.Command,vm.RepartitionCommand))==1,"更新划分仅一个入口");
                Require(!Walk(window).OfType<Button>().Any(button=>ReferenceEquals(button.Command,vm.GoTrainingCommand)||ReferenceEquals(button.Command,vm.GoModelsCommand)),"页面导航仅保留三页标签和快捷键");
                view.MoreOperations.IsExpanded=true;view.BoxCoordinates.IsExpanded=true;await Layout();Audit(window);
                Require(view.WorkspaceScroll.ScrollableHeight>0,"空间不足时提供工作区滚动");
                Require(view.DetectionImage.ActualHeight>=180,"展开低频区域保留原图视口");
                view.WorkspaceScroll.ScrollToBottom();await Layout();Capture(window,Path.Combine(output,$"expanded-{size.Width}.png"));
                view.MoreOperations.IsExpanded=false;view.BoxCoordinates.IsExpanded=false;view.WorkspaceScroll.ScrollToTop();
                vm.ActiveLearning.ShowThumbnailOverviewCommand.Execute(null);await Layout();
                Require(ActionVisual.GetIsSelected(view.ShowThumbnailOverviewButton)&&view.ThumbnailOverview.IsVisible,"缩略图模式保留选中状态");
                Capture(window,Path.Combine(output,$"thumbnails-{size.Width}.png"));
                vm.ActiveLearning.ShowImageEditorCommand.Execute(null);await Layout();
                Require(view.DetectionImage.IsImageDisplayed&&ActionVisual.GetIsSelected(view.ShowImageEditorButton),"恢复大图模式和原生图像");
                if(vm.ActiveLearning.SelectedRow is { } row)
                {
                    var oldBoxes=view.DetectionImage.Boxes;var oldClasses=view.DetectionImage.Categories;
                    var label=row.Image.FolderClassId??oldClasses!.First().Id;
                    view.DetectionImage.SetCurrentValue(DetectionImageView.CategoriesProperty,new[]{new DetectionCategory(label,new string('长',80))});
                    view.DetectionImage.SetCurrentValue(DetectionImageView.BoxesProperty,Enumerable.Range(0,4).Select(i=>new DetectionBox(label,.9,row.Image.Width*.2,row.Image.Height*.2,row.Image.Width*.8,row.Image.Height*.8)).ToArray());
                    await Layout();await Task.Delay(150);
                    var bounds=view.DetectionImage.LabelBounds;
                    Require(bounds.Count==4&&!bounds.Where((rect,index)=>bounds.Skip(index+1).Any(rect.IntersectsWith)).Any(),"重叠目标与长类别标签不重叠");
                    Capture(window,Path.Combine(output,$"labels-{size.Width}.png"));
                    view.DetectionImage.SetCurrentValue(DetectionImageView.BoxesProperty,new[]{new DetectionBox(label,0,double.NaN,0,10,10)});
                    await Layout();
                    Require(view.DetectionImage.IsImageDisplayed&&view.DetectionImage.LabelBounds.Count==0,"无效坐标编辑不破坏原图显示");
                    view.DetectionImage.SetCurrentValue(DetectionImageView.BoxesProperty,oldBoxes);
                    view.DetectionImage.SetCurrentValue(DetectionImageView.CategoriesProperty,oldClasses);
                }
            }
            var (classes,input)=DetectionSeedDialogs.CreateClassDialog();classes.Show();await Layout();Audit(classes);
            input.Text=new string('类',100);classes.UpdateLayout();
            Capture(classes,Path.Combine(output,"class-dialog.png"));classes.Close();
            var (parallel,choices,_)=DetectionParallelTrainingDialog.Create([new(1,new string('类',80),5,5),new(2,"第二类",5,5)],1,5,2,256);
            parallel.Show();await Layout();Audit(parallel);
            choices[1].IsChecked=true;
            Require(Walk(parallel).OfType<Button>().Single().IsEnabled,"独立窗口类别选择可启用操作");
            Capture(parallel,Path.Combine(output,"parallel-dialog.png"));parallel.Close();
            await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{passed=true,gpuTrainingExecuted=false,checks,controlCounts},new JsonSerializerOptions{WriteIndented=true}));
            if(manual)
            {
                vm.SelectedPageIndex=DesktopWorkspaceViewModel.DatasetPage;window.DatasetPages.SelectedIndex=1;
                window.Width=1380;window.Height=920;await Layout();
                ObserveHover(output);
                // Only the explicit validation mode opens this dialog; no dataset or model is written.
                var (live,_)=DetectionSeedDialogs.CreateClassDialog();live.ShowDialog();
                var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed+=(_,_)=>closed.TrySetResult();
                await closed.Task;
            }
            return 0;
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString(),checks,controlCounts},new JsonSerializerOptions{WriteIndented=true}));
            Capture(window,Path.Combine(output,"failed.png"));return 1;
        }
    }
    private static void ObserveHover(string output)
    {
        var entered=new Dictionary<DependencyObject,long>();
        void Enter(object sender,MouseEventArgs args)
        {
            if(sender is DependencyObject element)entered[element]=Stopwatch.GetTimestamp();
        }
        EventManager.RegisterClassHandler(typeof(ButtonBase),Mouse.MouseEnterEvent,new MouseEventHandler(Enter),true);
        EventManager.RegisterClassHandler(typeof(TabItem),Mouse.MouseEnterEvent,new MouseEventHandler(Enter),true);
        EventManager.RegisterClassHandler(typeof(ToolTip),ToolTip.OpenedEvent,new RoutedEventHandler((sender,_)=>
        {
            var tip=(ToolTip)sender;var target=tip.PlacementTarget;
            var elapsed=target is not null&&entered.TryGetValue(target,out var start)?Stopwatch.GetElapsedTime(start).TotalMilliseconds:(double?)null;
            File.AppendAllText(Path.Combine(output,"hover-events.jsonl"),JsonSerializer.Serialize(new{at=DateTimeOffset.Now,content=tip.Content?.ToString(),elapsedMs=elapsed,window=target is not null?Window.GetWindow(target)?.Title:null})+Environment.NewLine);
        }));
    }
}
