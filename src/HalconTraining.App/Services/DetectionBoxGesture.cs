namespace HalconTraining.App.Services;

public enum DetectionGestureKind { None, Draw, Move, Resize }
public sealed record DetectionBoxEdit(int Index, double Col1, double Row1, double Col2, double Row2);

/// <summary>Original-pixel pointer interaction; previews never modify stored annotations.</summary>
public sealed class DetectionBoxGesture
{
    private double _startX, _startY, _width, _height;
    private DetectionBox? _original;
    private bool _left, _right, _top, _bottom;
    public DetectionGestureKind Kind { get; private set; }
    public int Index { get; private set; } = -1;
    public DetectionBox? Preview { get; private set; }
    public bool IsActive => Kind != DetectionGestureKind.None;

    public void Begin(double x, double y, double width, double height, IReadOnlyList<DetectionBox> boxes,
        double tolerance, bool forceDraw = false, int selectedIndex = -1)
    {
        Cancel();
        if (!double.IsFinite(x) || !double.IsFinite(y) || width <= 0 || height <= 0) return;
        _width = width; _height = height;
        _startX = Math.Clamp(x, 0, width); _startY = Math.Clamp(y, 0, height);
        if (!forceDraw)
        {
            var indexes = Enumerable.Range(0, boxes.Count).Reverse().ToList();
            if (selectedIndex >= 0 && selectedIndex < boxes.Count) { indexes.Remove(selectedIndex); indexes.Insert(0, selectedIndex); }
            foreach (var index in indexes)
            {
                var box = boxes[index];
                if (x < box.Col1 - tolerance || x > box.Col2 + tolerance || y < box.Row1 - tolerance || y > box.Row2 + tolerance) continue;
                Index = index; _original = box; Preview = box;
                _left = Math.Abs(x - box.Col1) <= tolerance;
                _right = !_left && Math.Abs(x - box.Col2) <= tolerance;
                _top = Math.Abs(y - box.Row1) <= tolerance;
                _bottom = !_top && Math.Abs(y - box.Row2) <= tolerance;
                Kind = _left || _right || _top || _bottom ? DetectionGestureKind.Resize : DetectionGestureKind.Move;
                return;
            }
        }
        Kind = DetectionGestureKind.Draw;
        Preview = new DetectionBox(0, 0, _startX, _startY, _startX, _startY);
    }

    public void Update(double x, double y)
    {
        if (!IsActive || !double.IsFinite(x) || !double.IsFinite(y)) return;
        x = Math.Clamp(x, 0, _width); y = Math.Clamp(y, 0, _height);
        if (Kind == DetectionGestureKind.Draw)
        {
            Preview = new DetectionBox(0, 0, Math.Min(_startX, x), Math.Min(_startY, y), Math.Max(_startX, x), Math.Max(_startY, y));
            return;
        }
        var box = _original!;
        if (Kind == DetectionGestureKind.Move)
        {
            var dx = Math.Clamp(x - _startX, -box.Col1, _width - box.Col2);
            var dy = Math.Clamp(y - _startY, -box.Row1, _height - box.Row2);
            Preview = box with { Col1 = box.Col1 + dx, Col2 = box.Col2 + dx, Row1 = box.Row1 + dy, Row2 = box.Row2 + dy };
            return;
        }
        var left = _left ? x : box.Col1; var right = _right ? x : box.Col2;
        var top = _top ? y : box.Row1; var bottom = _bottom ? y : box.Row2;
        Preview = box with { Col1 = Math.Min(left, right), Col2 = Math.Max(left, right), Row1 = Math.Min(top, bottom), Row2 = Math.Max(top, bottom) };
    }

    public void Cancel() { Kind = DetectionGestureKind.None; Index = -1; Preview = null; _original = null; }
}
