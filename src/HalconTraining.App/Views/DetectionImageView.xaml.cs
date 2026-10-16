using System.IO;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using HalconTraining.App.Services;

namespace HalconTraining.App.Views;

/// <summary>Read-only original image with editable rectangle1 review overlays.</summary>
public partial class DetectionImageView : UserControl
{
    public static readonly DependencyProperty ImagePathProperty =
        DependencyProperty.Register(nameof(ImagePath), typeof(string), typeof(DetectionImageView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty BoxesProperty =
        DependencyProperty.Register(nameof(Boxes), typeof(IReadOnlyList<DetectionBox>), typeof(DetectionImageView), new PropertyMetadata(null, Changed));
    public string? ImagePath { get => (string?)GetValue(ImagePathProperty); set => SetValue(ImagePathProperty, value); }
    public IReadOnlyList<DetectionBox>? Boxes { get => (IReadOnlyList<DetectionBox>?)GetValue(BoxesProperty); set => SetValue(BoxesProperty, value); }
    private HImage? _image;
    private string? _loadedPath;
    private bool _ready;

    public DetectionImageView()
    {
        InitializeComponent();
        Display.HInitWindow += (_, _) => { _ready = true; Render(); };
        Loaded += (_, _) => Render();
        Unloaded += (_, _) => Release();
        SizeChanged += (_, _) => Render();
    }

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        ((DetectionImageView)target).Render();

    private void Release()
    {
        _image?.Dispose();
        _image = null;
        _loadedPath = null;
    }

    private void Render()
    {
        if (!_ready || !IsLoaded) return;
        try
        {
            var window = Display.HalconWindow;
            window.ClearWindow();
            if (!File.Exists(ImagePath))
            {
                Release();
                Message.Text = "选择目标检测图片";
                Message.Visibility = Visibility.Visible;
                return;
            }

            if (_loadedPath != ImagePath)
            {
                Release();
                _image = new HImage(ImagePath);
                _loadedPath = ImagePath;
            }

            _image!.GetImageSize(out int width, out int height);
            var ratio = Math.Max(1, Display.ActualWidth) / Math.Max(1, Display.ActualHeight);
            var visibleWidth = Math.Max(width, height * ratio);
            var visibleHeight = visibleWidth / ratio;
            window.SetPart((height - visibleHeight) / 2, (width - visibleWidth) / 2,
                (height + visibleHeight) / 2 - 1, (width + visibleWidth) / 2 - 1);
            using (var channels = _image.CountChannels())
            {
                if (channels.I == 3) window.DispColor(_image);
                else window.DispImage(_image);
            }

            window.SetDraw("margin");
            window.SetLineWidth(2);
            window.SetColor("#00E1FF");
            foreach (var box in Boxes ?? [])
            {
                if (!double.IsFinite(box.Col1) || !double.IsFinite(box.Col2) ||
                    !double.IsFinite(box.Row1) || !double.IsFinite(box.Row2) ||
                    box.Col1 < 0 || box.Row1 < 0 || box.Col2 <= box.Col1 || box.Row2 <= box.Row1)
                    continue; // In-progress edits can be temporarily invalid.
                HOperatorSet.GenRectangle1(out HObject rectangle, box.Row1, box.Col1, box.Row2, box.Col2);
                using (rectangle) window.DispRegion(rectangle);
                window.DispText($"类别 {box.ClassId} · {box.Confidence:P0}", "image",
                    Math.Max(0, box.Row1 - 18), box.Col1, "cyan", new HTuple(), new HTuple());
            }
            Message.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is HalconException or IOException or ArgumentException)
        {
            Message.Text = "无法显示检测图片：" + error.Message;
            Message.Visibility = Visibility.Visible;
            Release();
        }
    }
}
