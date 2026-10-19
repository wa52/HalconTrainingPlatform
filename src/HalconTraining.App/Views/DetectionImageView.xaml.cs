using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HalconDotNet;
using HalconTraining.App.Services;

namespace HalconTraining.App.Views;

public sealed record DetectionDrawnBox(double Col1, double Row1, double Col2, double Row2);

/// <summary>Native HALCON image with a WPF input layer for drawing and editing original-pixel boxes.</summary>
public partial class DetectionImageView : UserControl
{
    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(nameof(ImagePath), typeof(string), typeof(DetectionImageView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty BoxesProperty = DependencyProperty.Register(nameof(Boxes), typeof(IReadOnlyList<DetectionBox>), typeof(DetectionImageView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty CategoriesProperty = DependencyProperty.Register(nameof(Categories), typeof(IReadOnlyList<DetectionCategory>), typeof(DetectionImageView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty DrawBoxCommandProperty = DependencyProperty.Register(nameof(DrawBoxCommand), typeof(ICommand), typeof(DetectionImageView));
    public static readonly DependencyProperty EditBoxCommandProperty = DependencyProperty.Register(nameof(EditBoxCommand), typeof(ICommand), typeof(DetectionImageView));
    public static readonly DependencyProperty SelectedBoxIndexProperty = DependencyProperty.Register(nameof(SelectedBoxIndex), typeof(int), typeof(DetectionImageView), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, SelectionChanged));
    public ICommand? DrawBoxCommand { get => (ICommand?)GetValue(DrawBoxCommandProperty); set => SetValue(DrawBoxCommandProperty, value); }
    public ICommand? EditBoxCommand { get => (ICommand?)GetValue(EditBoxCommandProperty); set => SetValue(EditBoxCommandProperty, value); }
    public int SelectedBoxIndex { get => (int)GetValue(SelectedBoxIndexProperty); set => SetValue(SelectedBoxIndexProperty, value); }
    public string? ImagePath { get => (string?)GetValue(ImagePathProperty); set => SetValue(ImagePathProperty, value); }
    public IReadOnlyList<DetectionBox>? Boxes { get => (IReadOnlyList<DetectionBox>?)GetValue(BoxesProperty); set => SetValue(BoxesProperty, value); }
    public IReadOnlyList<DetectionCategory>? Categories { get => (IReadOnlyList<DetectionCategory>?)GetValue(CategoriesProperty); set => SetValue(CategoriesProperty, value); }
    private readonly DetectionBoxGesture _gesture = new(), _hover = new();
    private HImage? _image;
    private string? _loadedPath;
    private bool _ready, _fitNeeded = true;
    private int _originalWidth, _originalHeight;
    private double _partTop, _partLeft, _partWidth = 1, _partHeight = 1;
    private Point? _panStart;
    private double _panTop, _panLeft;
    public bool IsImageDisplayed => _image is not null && Message.Visibility == Visibility.Collapsed;
    public bool IsGestureActive => _gesture.IsActive;

    public DetectionImageView()
    {
        InitializeComponent();
        Display.HInitWindow += (_, _) => { _ready = true; _fitNeeded = true; Render(); };
        Loaded += (_, _) => Render();
        Unloaded += (_, _) => Release();
        InputCanvas.SizeChanged += (_, _) => { _fitNeeded = true; CancelInput(); Render(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelInput(); };
        InputCanvas.MouseLeftButtonDown += BeginBox;
        InputCanvas.MouseMove += MovePointer;
        InputCanvas.MouseLeftButtonUp += CompleteBox;
        InputCanvas.MouseRightButtonDown += (_, args) =>
        {
            if (_image is null || _gesture.IsActive) return;
            InputCanvas.Focus(); ReadViewport();
            _panStart = args.GetPosition(InputCanvas); _panTop = _partTop; _panLeft = _partLeft;
            InputCanvas.CaptureMouse(); InputCanvas.Cursor = Cursors.Hand; args.Handled = true;
        };
        InputCanvas.MouseRightButtonUp += (_, args) => { if (_panStart is null) return; CancelInput(); args.Handled = true; };
        InputCanvas.LostMouseCapture += (_, _) => { _gesture.Cancel(); _panStart = null; Render(); };
        InputCanvas.MouseWheel += Zoom;
        InputCanvas.KeyDown += (_, args) => { if (args.Key == Key.Escape) { CancelInput(); args.Handled = true; } };
    }

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((DetectionImageView)target).Render();
    private static void SelectionChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((DetectionImageView)target).DrawAdornments();

    private void BeginBox(object sender, MouseButtonEventArgs args)
    {
        if (_image is null || !IsEnabled || _panStart is not null) return;
        InputCanvas.Focus(); ReadViewport();
        var pixel = args.GetPosition(InputCanvas);
        var image = ToImage(pixel);
        var tolerance = 7 * Math.Max(_partWidth / Math.Max(1, InputCanvas.ActualWidth), _partHeight / Math.Max(1, InputCanvas.ActualHeight));
        _gesture.Begin(image.X, image.Y, _originalWidth, _originalHeight, Boxes ?? [], tolerance,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), SelectedBoxIndex);
        SetCurrentValue(SelectedBoxIndexProperty, _gesture.Index);
        InputCanvas.CaptureMouse(); Render(); args.Handled = true;
    }

    private void MovePointer(object sender, MouseEventArgs args)
    {
        if (_image is null) return;
        var pixel = args.GetPosition(InputCanvas);
        if (_panStart is { } start)
        {
            SetViewport(_panTop - (pixel.Y - start.Y) / Math.Max(1, InputCanvas.ActualHeight) * _partHeight,
                _panLeft - (pixel.X - start.X) / Math.Max(1, InputCanvas.ActualWidth) * _partWidth, _partWidth, _partHeight);
            Render(); args.Handled = true; return;
        }
        var image = ToImage(pixel);
        if (_gesture.IsActive)
        {
            _gesture.Update(image.X, image.Y); DrawAdornments(); args.Handled = true; return;
        }
        var tolerance = 7 * Math.Max(_partWidth / Math.Max(1, InputCanvas.ActualWidth), _partHeight / Math.Max(1, InputCanvas.ActualHeight));
        _hover.Begin(image.X, image.Y, _originalWidth, _originalHeight, Boxes ?? [], tolerance,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), SelectedBoxIndex);
        InputCanvas.Cursor = _hover.Kind switch { DetectionGestureKind.Move => Cursors.SizeAll, DetectionGestureKind.Resize => Cursors.SizeNWSE, _ => Cursors.Cross };
    }

    private void CompleteBox(object sender, MouseButtonEventArgs args)
    {
        if (!_gesture.IsActive) return;
        var point = ToImage(args.GetPosition(InputCanvas)); _gesture.Update(point.X, point.Y);
        var kind = _gesture.Kind; var index = _gesture.Index; var box = _gesture.Preview;
        _gesture.Cancel(); InputCanvas.ReleaseMouseCapture();
        if (box is not null && box.Col2 - box.Col1 >= 2 && box.Row2 - box.Row1 >= 2)
        {
            if (kind == DetectionGestureKind.Draw)
            {
                var request = new DetectionDrawnBox(box.Col1, box.Row1, box.Col2, box.Row2);
                if (DrawBoxCommand?.CanExecute(request) == true) DrawBoxCommand.Execute(request);
            }
            else
            {
                var request = new DetectionBoxEdit(index, box.Col1, box.Row1, box.Col2, box.Row2);
                if (EditBoxCommand?.CanExecute(request) == true) EditBoxCommand.Execute(request);
            }
        }
        Render(); args.Handled = true;
    }

    private void CancelInput()
    {
        _gesture.Cancel(); _panStart = null;
        if (InputCanvas.IsMouseCaptured) InputCanvas.ReleaseMouseCapture();
        InputCanvas.Cursor = Cursors.Cross; Render();
    }

    private void Zoom(object sender, MouseWheelEventArgs args)
    {
        if (_image is null || _gesture.IsActive || _panStart is not null) return;
        ReadViewport();
        var pixel = args.GetPosition(InputCanvas); var anchor = ToImage(pixel);
        var factor = Math.Pow(.8, args.Delta / 120.0);
        var width = _partWidth * factor; var height = _partHeight * factor;
        if (Math.Min(width, height) < 8 || Math.Max(width, height) > Math.Max(_originalWidth, _originalHeight) * 20) return;
        SetViewport(anchor.Y - pixel.Y / Math.Max(1, InputCanvas.ActualHeight) * height,
            anchor.X - pixel.X / Math.Max(1, InputCanvas.ActualWidth) * width, width, height);
        Render(); args.Handled = true;
    }

    private void FitClicked(object sender, RoutedEventArgs args) { _fitNeeded = true; CancelInput(); }
    private void ReadViewport()
    {
        if (!_ready || _image is null) return;
        Display.HalconWindow.GetPart(out int top, out int left, out int bottom, out int right);
        _partTop = top; _partLeft = left; _partWidth = right - left + 1; _partHeight = bottom - top + 1;
    }
    private void SetViewport(double top, double left, double width, double height)
    {
        _partTop = top; _partLeft = left; _partWidth = width; _partHeight = height;
        Display.HalconWindow.SetPart(top, left, top + height - 1, left + width - 1);
    }
    private Point ToImage(Point pixel) => new(_partLeft + pixel.X / Math.Max(1, InputCanvas.ActualWidth) * _partWidth,
        _partTop + pixel.Y / Math.Max(1, InputCanvas.ActualHeight) * _partHeight);
    private Point ToCanvas(double x, double y) => new((x - _partLeft) / _partWidth * InputCanvas.ActualWidth, (y - _partTop) / _partHeight * InputCanvas.ActualHeight);

    private void DrawAdornments()
    {
        if (InputCanvas is null) return;
        InputCanvas.Children.Clear();
        if (_image is null) return;
        DrawLabels();
        var selected = SelectedBoxIndex >= 0 && SelectedBoxIndex < (Boxes?.Count ?? 0) ? Boxes![SelectedBoxIndex] : null;
        var box = _gesture.IsActive ? _gesture.Preview : selected;
        if (box is null || !CanDrawBox(box)) return;
        var first = ToCanvas(box.Col1, box.Row1); var last = ToCanvas(box.Col2, box.Row2);
        var rectangle = new Rectangle { Width = Math.Max(0, last.X - first.X), Height = Math.Max(0, last.Y - first.Y),
            Stroke = Brushes.Gold, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(24, 255, 210, 80)), IsHitTestVisible = false };
        Canvas.SetLeft(rectangle, first.X); Canvas.SetTop(rectangle, first.Y); InputCanvas.Children.Add(rectangle);
        if (_gesture.Kind == DetectionGestureKind.Draw) return;
        foreach (var (x, y) in new[] { (first.X, first.Y), ((first.X + last.X) / 2, first.Y), (last.X, first.Y),
            (first.X, (first.Y + last.Y) / 2), (last.X, (first.Y + last.Y) / 2),
            (first.X, last.Y), ((first.X + last.X) / 2, last.Y), (last.X, last.Y) })
        {
            var handle = new Rectangle { Width = 8, Height = 8, Fill = Brushes.White, Stroke = Brushes.Goldenrod, StrokeThickness = 1, IsHitTestVisible = false };
            Canvas.SetLeft(handle, x - 4); Canvas.SetTop(handle, y - 4); InputCanvas.Children.Add(handle);
        }
    }

    public IReadOnlyList<Rect> LabelBounds {get;private set;}=[];
    private bool CanDrawBox(DetectionBox box)=>double.IsFinite(box.Col1)&&double.IsFinite(box.Row1)&&
        double.IsFinite(box.Col2)&&double.IsFinite(box.Row2)&&box.Col1>=0&&box.Row1>=0&&
        box.Col2>box.Col1&&box.Row2>box.Row1&&box.Col2<=_originalWidth&&box.Row2<=_originalHeight;
    private void DrawLabels()
    {
        var bounds=new List<Rect>();
        var width=InputCanvas.ActualWidth;var height=InputCanvas.ActualHeight;
        if(width<40||height<24){LabelBounds=bounds;return;}
        var boxes=Boxes??[];
        for(var index=0;index<boxes.Count;index++)
        {
            var box=_gesture.IsActive&&_gesture.Index==index?_gesture.Preview:boxes[index];
            if(box is null||!CanDrawBox(box))continue;
            var start=ToCanvas(box.Col1,box.Row1);var end=ToCanvas(box.Col2,box.Row2);
            if(end.X<0||end.Y<0||start.X>width||start.Y>height)continue;
            var name=Categories?.FirstOrDefault(c=>c.Id==box.ClassId)?.Name??$"类别 {box.ClassId}";
            var text=new TextBlock{Text=$"{index+1} · {name}"+(box.Confidence>0?$" · {box.Confidence:P0}":""),
                Foreground=Brushes.Cyan,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=Math.Min(260,width-8),Padding=new Thickness(4,2,4,2)};
            text.Measure(new Size(text.MaxWidth,30));
            var size=text.DesiredSize;
            var x=Math.Clamp(start.X,2,Math.Max(2,width-size.Width-2));
            Rect? placement=null;
            // Prefer the space beside this box; pack into free lanes only when necessary.
            var candidates=new[]{start.Y-size.Height-2,end.Y+2,start.Y+2}
                .Concat(Enumerable.Range(0,Math.Max(1,(int)(height/(size.Height+3)))).Select(i=>2+i*(size.Height+3)));
            foreach(var y in candidates)
            {
                var rect=new Rect(x,Math.Clamp(y,2,Math.Max(2,height-size.Height-2)),size.Width,size.Height);
                if(bounds.All(other=>!other.IntersectsWith(rect))){placement=rect;break;}
            }
            // Dense labels remain available in the coordinate table instead of overlapping.
            if(placement is not { } chosen)continue;
            var label=new Border{Child=text,Background=new SolidColorBrush(Color.FromArgb(220,24,35,44)),IsHitTestVisible=false};
            Canvas.SetLeft(label,chosen.X);Canvas.SetTop(label,chosen.Y);InputCanvas.Children.Add(label);bounds.Add(chosen);
        }
        LabelBounds=bounds;
    }

    private void Release()
    {
        _gesture.Cancel(); _panStart = null;
        if (InputCanvas.IsMouseCaptured) InputCanvas.ReleaseMouseCapture();
        _image?.Dispose(); _image = null; _loadedPath = null; _fitNeeded = true;
        InputCanvas.Children.Clear();
        LabelBounds=[];
    }

    private void Render()
    {
        if (!_ready || !IsLoaded) return;
        try
        {
            var window = Display.HalconWindow;
            window.ClearWindow();
            if (!File.Exists(ImagePath))
            { Release(); Message.Text = "选择目标检测图片"; Message.Visibility = Visibility.Visible; return; }
            if (_loadedPath != ImagePath)
            {
                Release(); _image = new HImage(ImagePath); _loadedPath = ImagePath;
                _image.GetImageSize(out _originalWidth, out _originalHeight);
            }
            if (_fitNeeded)
            {
                var ratio = Math.Max(1, InputCanvas.ActualWidth) / Math.Max(1, InputCanvas.ActualHeight);
                var width = Math.Max(_originalWidth, _originalHeight * ratio); var height = width / ratio;
                SetViewport((_originalHeight - height) / 2, (_originalWidth - width) / 2, width, height);
                _fitNeeded = false;
            }
            ReadViewport();
            using (var channels = _image!.CountChannels())
            { if (channels.I == 3) window.DispColor(_image); else window.DispImage(_image); }
            window.SetDraw("margin"); window.SetLineWidth(2); window.SetColor("#00E1FF");
            var boxes = Boxes ?? [];
            for (var index = 0; index < boxes.Count; index++)
            {
                if (_gesture.IsActive && _gesture.Index == index) continue;
                var box = boxes[index];
                if (!CanDrawBox(box)) continue;
                using var rectangle = new HRegion(); rectangle.GenRectangle1(box.Row1, box.Col1, box.Row2, box.Col2);
                window.DispRegion(rectangle);
            }
            Message.Visibility = Visibility.Collapsed; DrawAdornments();
        }
        catch (Exception error) when (error is HalconException or IOException or ArgumentException)
        { Message.Text = "无法显示检测图片：" + error.Message; Message.Visibility = Visibility.Visible; Release(); }
    }
}
