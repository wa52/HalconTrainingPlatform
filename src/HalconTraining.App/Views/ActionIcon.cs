using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HalconTraining.App.Views;

/// <summary>Shared 24-unit outline icons; independent of installed symbol fonts.</summary>
public sealed class ActionIcon : FrameworkElement
{
    private static readonly IReadOnlyDictionary<string, Geometry> Shapes = new Dictionary<string, string>
    {
        ["Dataset"] = "M3,6 C3,2 21,2 21,6 C21,10 3,10 3,6 M3,6 V18 C3,22 21,22 21,18 V6 M3,12 C3,16 21,16 21,12",
        ["Train"] = "M8,4 L20,12 8,20 Z",
        ["TrainCombined"] = "M2,4 L8,9 M2,20 L8,15 M2,12 H8 M11,5 L22,12 11,19 Z",
        ["Model"] = "M12,2 L22,7 22,17 12,22 2,17 2,7 Z M2,7 L12,12 22,7 M12,12 V22",
        ["Open"] = "M3,19 V5 H10 L13,8 H21 V11 M3,19 L7,11 H23 L19,20 H3 Z",
        ["ImportFolder"] = "M3,10 V5 H10 L13,8 H21 V20 H3 V16 M1,13 H13 M9,9 L13,13 9,17",
        ["New"] = "M14,2 H5 V22 H19 V7 Z M14,2 V7 H19 M8,14 H16 M12,10 V18",
        ["AddImages"] = "M18,13 V20 H2 V4 H17 M4,17 L8,12 11,15 14,11 18,15 M17,6 H23 M20,3 V9",
        ["RemoveImage"] = "M18,13 V20 H2 V4 H17 M4,17 L8,12 11,15 14,11 18,15 M17,6 H23",
        ["Export"] = "M3,13 V21 H21 V13 M12,17 V2 M7,7 L12,2 17,7",
        ["ExportClass"] = "M2,14 V21 H18 V14 M10,17 V3 M5,8 L10,3 15,8 M19,4 H22 V10 H19",
        ["ExportCoco"] = "M3,13 V21 H21 V13 M12,17 V2 M7,7 L12,2 17,7 M3,5 V9 H6 M18,9 H21 V5",
        ["Save"] = "M3,3 H18 L21,6 V21 H3 Z M7,3 V9 H17 V3 M7,21 V14 H17 V21",
        ["Stop"] = "M5,5 H19 V19 H5 Z",
        ["Refresh"] = "M20,9 C18,2 6,2 3,10 M3,4 V10 H9 M4,15 C6,22 18,22 21,14 M21,20 V14 H15",
        ["Settings"] = "M3,6 H21 M3,12 H21 M3,18 H21 M8,3 V9 M16,9 V15 M10,15 V21",
        ["Check"] = "M20,5 L9,18 3,12",
        ["CheckAll"] = "M14,5 L6,14 2,10 M22,9 L13,19 9,15",
        ["Back"] = "M16,4 L8,12 16,20",
        ["Next"] = "M8,4 L16,12 8,20",
        ["Cancel"] = "M5,5 L19,19 M19,5 L5,19",
        ["Delete"] = "M3,6 H21 M8,6 V3 H16 V6 M5,6 L6,21 H18 L19,6 M10,10 V17 M14,10 V17",
        ["Draw"] = "M3,3 H15 M3,3 V21 H21 V15 M10,14 L18,6 22,10 14,18 9,19 Z M16,8 L20,12",
        ["AddBox"] = "M3,3 H17 V17 H3 Z M15,19 H23 M19,15 V23",
        ["Rework"] = "M12,2 L23,21 H1 Z M12,8 V14 M12,17 V18",
        ["Grid"] = "M3,3 H9 V9 H3 Z M15,3 H21 V9 H15 Z M3,15 H9 V21 H3 Z M15,15 H21 V21 H15 Z",
        ["Fit"] = "M3,9 V3 H9 M15,3 H21 V9 M21,15 V21 H15 M9,21 H3 V15 M8,8 H16 V16 H8 Z",
        ["Eye"] = "M1,12 C6,2 18,2 23,12 C18,22 6,22 1,12 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
        ["Predict"] = "M3,5 H21 V19 H3 Z M5,15 L9,10 13,14 18,8 M15,8 H18 V11",
        ["PredictSelected"] = "M3,3 H18 V18 H3 Z M6,14 L10,9 14,13 M8,22 H22 V8",
        ["PredictExisting"] = "M3,3 H12 V12 H3 Z M12,12 H21 V21 H12 Z M16,3 H21 V8 M18,6 L13,11",
        ["Parallel"] = "M7,3 L13,7 7,11 Z M7,13 L13,17 7,21 Z M17,3 V21 M20,3 V21",
        ["Folder"] = "M2,6 H10 L13,9 H22 V21 H2 Z M2,6 V3 H9 L12,6 H20 V9",
        ["ResultFolder"] = "M2,6 H10 L13,9 H22 V21 H2 Z M8,15 L11,18 17,12",
        ["History"] = "M3,11 A9,9 0 1 1 5,18 M3,4 V11 H10 M12,7 V12 L16,15",
        ["Details"] = "M4,3 H20 V21 H4 Z M8,7 H16 M8,12 H16 M8,17 H13",
        ["Classes"] = "M3,4 H9 V10 H3 Z M15,4 H21 V10 H15 Z M3,16 H9 V22 H3 Z M15,16 H21 V22 H15 Z M6,10 V16 M18,10 V16",
        ["Environment"] = "M7,7 H17 V17 H7 Z M3,8 H7 M3,16 H7 M17,8 H21 M17,16 H21 M8,3 V7 M16,3 V7 M8,17 V21 M16,17 V21 M10,12 L12,14 15,10",
        ["Log"] = "M5,3 H19 V21 H5 Z M8,7 L11,10 8,13 M12,15 H16",
        ["Split"] = "M12,2 V8 M4,20 V13 H20 V20 M12,8 V20 M4,20 L2,17 M4,20 L6,17 M12,20 L10,17 M12,20 L14,17 M20,20 L18,17 M20,20 L22,17",
        ["LoadModel"] = "M4,4 H14 V14 H4 Z M14,14 H21 V21 H14 Z M3,20 H10 M7,17 L10,20 7,23",
        ["AutoModel"] = "M7,2 L9,7 14,9 9,11 7,16 5,11 0,9 5,7 Z M14,13 H22 V21 H14 Z",
        ["Metrics"] = "M3,3 V21 H22 M7,17 V12 M12,17 V7 M17,17 V4",
    }.ToDictionary(pair => pair.Key, pair => { var shape=Geometry.Parse(pair.Value);shape.Freeze();return shape; });

    public static readonly DependencyProperty KindProperty=DependencyProperty.Register(nameof(Kind),typeof(string),typeof(ActionIcon),new FrameworkPropertyMetadata("Details",FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty=Control.ForegroundProperty.AddOwner(typeof(ActionIcon),new FrameworkPropertyMetadata(Brushes.Black,FrameworkPropertyMetadataOptions.Inherits|FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind {get=>(string)GetValue(KindProperty);set=>SetValue(KindProperty,value);}
    public Brush Foreground {get=>(Brush)GetValue(ForegroundProperty);set=>SetValue(ForegroundProperty,value);}
    public static bool Exists(string kind)=>Shapes.ContainsKey(kind);
    protected override Size MeasureOverride(Size availableSize)=>new(20,20);
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if(!Shapes.TryGetValue(Kind,out var geometry))return;
        drawingContext.PushTransform(new ScaleTransform(ActualWidth/24,ActualHeight/24));
        var pen=new Pen(Foreground,1.7){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
        drawingContext.DrawGeometry(null,pen,geometry);
        drawingContext.Pop();
    }
}
