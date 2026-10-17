using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace HalconTraining.App.Services;

/// <summary>Minimal native-WPF dialogs; no browser and no external annotation server.</summary>
public static class DetectionSeedDialogs
{
    public static IReadOnlyList<(string Path, int Width, int Height)> SelectImages(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title, Filter = "图像|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
            Multiselect = true, CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return [];
        var images = new List<(string Path, int Width, int Height)>();
        foreach (var filename in dialog.FileNames)
        {
            using var stream = File.OpenRead(filename);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            images.Add((Path.GetFullPath(filename), frame.PixelWidth, frame.PixelHeight));
        }
        return images;
    }

    public static string[]? EnterClasses()
    {
        var window = new Window
        {
            Title = "初始检测类别", Width = 420, Height = 220, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = System.Windows.Application.Current.MainWindow
        };
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = "请输入需要检测的类别，用英文逗号分隔。", FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });
        var input = new TextBox { Text = "缺陷", Margin = new Thickness(0, 12, 0, 8), FontSize = 14, Padding = new Thickness(8) };
        content.Children.Add(input);
        content.Children.Add(new TextBlock
        {
            Text = "示例：划痕,凹坑。每个类别至少需要人工确认一个检测框。",
            TextWrapping = TextWrapping.Wrap, Opacity = .7
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button { Content = "取消", MinWidth = 70, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => window.DialogResult = false;
        var confirm = new Button { Content = "创建标注", MinWidth = 96, IsDefault = true };
        confirm.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);
        window.Content = content;
        if (window.ShowDialog() != true) return null;
        var classes = input.Text.Split(new[] { ',', '，' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (classes.Length == 0) throw new InvalidDataException("至少需要一个类别。");
        return classes;
    }

    public static string? SelectDetector()
    {
        var dialog = new OpenFileDialog { Title = "选择已有 YOLO .pt 或 HALCON .hdl 检测模型",
            Filter = "目标检测模型|*.pt;*.hdl|YOLO 模型|*.pt|HALCON 模型|*.hdl", CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
