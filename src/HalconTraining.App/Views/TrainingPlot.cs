using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HalconTraining.App.Views;

public sealed record TrainingPoint(double Epoch,double Value);
public sealed class TrainingPlot : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty=DependencyProperty.Register(nameof(Points),typeof(IReadOnlyList<TrainingPoint>),typeof(TrainingPlot),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsPercentageProperty=DependencyProperty.Register(nameof(IsPercentage),typeof(bool),typeof(TrainingPlot),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<TrainingPoint>? Points {get=>(IReadOnlyList<TrainingPoint>?)GetValue(PointsProperty);set=>SetValue(PointsProperty,value);}
    public bool IsPercentage {get=>(bool)GetValue(IsPercentageProperty);set=>SetValue(IsPercentageProperty,value);}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);var data=Points?.Where(p=>double.IsFinite(p.Value)&&double.IsFinite(p.Epoch)).ToArray()??[];
        const double left=56,top=14,bottom=33;var width=Math.Max(1,ActualWidth-left-14);var height=Math.Max(1,ActualHeight-top-bottom);
        var maximum=IsPercentage?1:data.Length>0?Math.Max(.0001,data.Max(p=>p.Value)*1.1):1;
        var epochMaximum=data.Length>0?Math.Max(1,Math.Ceiling(data.Max(p=>p.Epoch))):1;
        var muted=new SolidColorBrush(Color.FromRgb(105,123,144));var line=new Pen(new SolidColorBrush(Color.FromRgb(226,233,240)),1);
        void Label(string value,double x,double y)=>dc.DrawText(new FormattedText(value,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),11,muted,VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y));
        for(var i=0;i<=4;i++){var y=top+height*i/4;dc.DrawLine(line,new Point(left,y),new Point(left+width,y));var value=maximum*(4-i)/4;Label(IsPercentage?value.ToString("P0"):value.ToString("G3",CultureInfo.InvariantCulture),2,y-7);}
        for(var i=0;i<=4;i++){var x=left+width*i/4;Label((epochMaximum*i/4).ToString("0.##",CultureInfo.InvariantCulture),x-5,top+height+8);}
        Label("Epoch",Math.Max(left,left+width-34),ActualHeight-17);
        if(data.Length==0){Label("训练开始后显示真实指标",left+20,top+height/2);return;}
        var geometry=new StreamGeometry();using(var context=geometry.Open())
        {
            for(var i=0;i<data.Length;i++){var point=new Point(left+width*data[i].Epoch/epochMaximum,top+height-height*data[i].Value/maximum);if(i==0)context.BeginFigure(point,false,false);else context.LineTo(point,true,false);}
        }
        var color=new SolidColorBrush(IsPercentage?Color.FromRgb(26,143,126):Color.FromRgb(36,100,162));dc.DrawGeometry(null,new Pen(color,2),geometry);
        foreach(var point in data.Where((_,i)=>IsPercentage||i==data.Length-1))dc.DrawEllipse(color,null,new Point(left+width*point.Epoch/epochMaximum,top+height-height*point.Value/maximum),3,3);
    }
}
