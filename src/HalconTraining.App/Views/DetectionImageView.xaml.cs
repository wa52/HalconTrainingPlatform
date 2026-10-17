using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HalconDotNet;
using HalconTraining.App.Services;

namespace HalconTraining.App.Views;

public sealed record DetectionDrawnBox(double Col1, double Row1, double Col2, double Row2);

/// <summary>Original image with rectangle1 overlays and drag-to-add boxes.</summary>
public partial class DetectionImageView : UserControl
{
    public static readonly DependencyProperty ImagePathProperty =
        DependencyProperty.Register(nameof(ImagePath), typeof(string), typeof(DetectionImageView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty BoxesProperty =
        DependencyProperty.Register(nameof(Boxes), typeof(IReadOnlyList<DetectionBox>), typeof(DetectionImageView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty DrawBoxCommandProperty =
        DependencyProperty.Register(nameof(DrawBoxCommand), typeof(ICommand), typeof(DetectionImageView));
    public ICommand? DrawBoxCommand { get => (ICommand?)GetValue(DrawBoxCommandProperty); set => SetValue(DrawBoxCommandProperty, value); }
    public string? ImagePath { get => (string?)GetValue(ImagePathProperty); set => SetValue(ImagePathProperty, value); }
    public IReadOnlyList<DetectionBox>? Boxes { get => (IReadOnlyList<DetectionBox>?)GetValue(BoxesProperty); set => SetValue(BoxesProperty, value); }
    private HImage? _image;
    private string? _loadedPath;
    private bool _ready;
    private Point? _mouseStart;
    private double _partTop, _partLeft, _partWidth, _partHeight;
    private int _originalWidth, _originalHeight;

    public DetectionImageView()
    {
        InitializeComponent();
        Display.HInitWindow += (_, _) => { _ready = true; Render(); };
        Loaded += (_, _) => Render();
        Unloaded += (_, _) => Release();
        SizeChanged += (_, _) => Render();
        Display.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (_image is null || DrawBoxCommand is null || !IsEnabled) return;
            _mouseStart = args.GetPosition(Display);
            Display.CaptureMouse();
            args.Handled = true;
        };
        Display.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (_mouseStart is not { } start) return;
            var end = args.GetPosition(Display);
            _mouseStart = null;
            Display.ReleaseMouseCapture();
            if (Math.Abs(end.X - start.X) < 5 || Math.Abs(end.Y - start.Y) < 5) return;
            var first = PointToImage(start);
            var last = PointToImage(end);
            var box = new DetectionDrawnBox(
                Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
                Math.Max(first.X, last.X), Math.Max(first.Y, last.Y));
            if (box.Col2 - box.Col1 >= 2 && box.Row2 - box.Row1 >= 2 &&
                DrawBoxCommand?.CanExecute(box) == true) DrawBoxCommand.Execute(box);
            args.Handled = true;
        };
        Display.LostMouseCapture += (_, _) => _mouseStart = null;
    }

    private Point PointToImage(Point pixel) =>
        new(Math.Clamp(_partLeft + pixel.X / Math.Max(1, Display.ActualWidth) * _partWidth, 0, _originalWidth),
            Math.Clamp(_partTop + pixel.Y / Math.Max(1, Display.ActualHeight) * _partHeight, 0, _originalHeight));

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        ((DetectionImageView)target).Render();

    private void Release()
    {
        _image?.Dispose();
        _image = null;
        _loadedPath = null;
        _mouseStart = null;
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
            _originalWidth = width;
            _originalHeight = height;
            _partTop = (height - visibleHeight) / 2;
            _partLeft = (width - visibleWidth) / 2;
            _partWidth = visibleWidth;
            _partHeight = visibleHeight;
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
                using var rectangle = new HRegion();
                rectangle.GenRectangle1(box.Row1, box.Col1, box.Row2, box.Col2);
                window.DispRegion(rectangle);
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
