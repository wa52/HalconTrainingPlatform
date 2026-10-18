using System.Windows;
using System.Windows.Controls;
using HalconTraining.App.ViewModels;

namespace HalconTraining.App.Views;

public sealed record DetectionParallelSelection(int[] ClassIds, int Concurrency);

public static class DetectionParallelTrainingDialog
{
    public static DetectionParallelSelection? Select(IReadOnlyList<DetectionFolderFilter> categories, int? currentId,
        int epochs, int batch, int imageSize)
    {
        var window = new Window { Title = "并行训练类别", Width = 440, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = System.Windows.Application.Current.MainWindow };
        var layout = new DockPanel { Margin = new Thickness(20) };
        var heading = new TextBlock { Text = $"选择至少两个类别，使用当前参数：\n{imageSize} × {imageSize} · Batch {batch} · {epochs} Epoch\n每个类别独立生成训练快照和模型。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,14) };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        footer.Children.Add(new TextBlock { Text = "同时运行数（单显卡共享显存）", Margin = new Thickness(0,12,0,6) });
        var concurrency = new ComboBox { ItemsSource = new[] { 2, 1 }, SelectedIndex = 0, MinHeight = 30 };
        footer.Children.Add(concurrency);
        var begin = new Button { Content = "开始并行训练", MinHeight = 36, Margin = new Thickness(0,14,0,0), IsEnabled = false };
        footer.Children.Add(begin);
        var choices = categories.Select(category => new CheckBox { Content = $"{category.Name} · 已确认 {category.Approved} 张",
            Tag = category.Id, IsChecked = category.Id == currentId, Margin = new Thickness(0,7,0,7) }).ToArray();
        foreach (var choice in choices)
        {
            choice.Checked += (_,_) => begin.IsEnabled = choices.Count(item => item.IsChecked == true) >= 2;
            choice.Unchecked += (_,_) => begin.IsEnabled = choices.Count(item => item.IsChecked == true) >= 2;
        }
        var list = new StackPanel(); foreach (var choice in choices) list.Children.Add(choice);
        layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        begin.Click += (_,_) => window.DialogResult = true; window.Content = layout;
        return window.ShowDialog() == true
            ? new DetectionParallelSelection(choices.Where(item => item.IsChecked == true).Select(item => (int)item.Tag).ToArray(), (int)concurrency.SelectedItem)
            : null;
    }
}
