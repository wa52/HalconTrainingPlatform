using System.IO;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
namespace HalconTraining.App.Views;
public partial class HalconImageView : UserControl
{
    public static readonly DependencyProperty ImagePathProperty=DependencyProperty.Register(nameof(ImagePath),typeof(string),typeof(HalconImageView),new PropertyMetadata(null,Changed));
    public static readonly DependencyProperty CaptionProperty=DependencyProperty.Register(nameof(Caption),typeof(string),typeof(HalconImageView),new PropertyMetadata(null,Changed));
    public string? Caption {get=>(string?)GetValue(CaptionProperty);set=>SetValue(CaptionProperty,value);}
    public static readonly DependencyProperty MaskPathProperty=DependencyProperty.Register(nameof(MaskPath),typeof(string),typeof(HalconImageView),new PropertyMetadata(null,Changed));
    public static readonly DependencyProperty OpacityPercentProperty=DependencyProperty.Register(nameof(OpacityPercent),typeof(double),typeof(HalconImageView),new PropertyMetadata(60d,Changed));
    public string? ImagePath {get=>(string?)GetValue(ImagePathProperty);set=>SetValue(ImagePathProperty,value);}
    public string? MaskPath {get=>(string?)GetValue(MaskPathProperty);set=>SetValue(MaskPathProperty,value);}
    public double OpacityPercent {get=>(double)GetValue(OpacityPercentProperty);set=>SetValue(OpacityPercentProperty,value);}
    private HImage? _image;private HRegion? _region;private string? _loadedPath,_loadedMask;private bool _ready;
    public bool IsImageDisplayed=>_image is not null&&Message.Visibility==Visibility.Collapsed;
    public string? LastDisplayError {get;private set;}
    public HalconImageView(){InitializeComponent();Display.HInitWindow+=(_,_)=>{_ready=true;Render();};Loaded+=(_,_)=>Render();Unloaded+=(_,_)=>Release();SizeChanged+=(_,_)=>Render();}
    private static void Changed(DependencyObject target,DependencyPropertyChangedEventArgs e)=>((HalconImageView)target).Render();
    private void Release(){_region?.Dispose();_region=null;_image?.Dispose();_image=null;_loadedPath=null;_loadedMask=null;}
    private void Render()
    {
        if(!_ready||!IsLoaded)return;
        try
        {
            var window=Display.HalconWindow;window.ClearWindow();
            if(!File.Exists(ImagePath)){Release();Message.Text="选择图像后显示";Message.Visibility=Visibility.Visible;return;}
            if(_loadedPath!=ImagePath||_loadedMask!=MaskPath){Release();_image=new HImage(ImagePath);_loadedPath=ImagePath;_loadedMask=MaskPath;if(File.Exists(MaskPath)){using var mask=new HImage(MaskPath);_region=mask.Threshold(1d,255d);}}
            _image!.GetImageSize(out int width,out int height);
            var aspect=Math.Max(1,Display.ActualWidth)/Math.Max(1,Display.ActualHeight);
            var visibleWidth=Math.Max(width,height*aspect);var visibleHeight=visibleWidth/aspect;
            window.SetPart((height-visibleHeight)/2,(width-visibleWidth)/2,(height+visibleHeight)/2-1,(width+visibleWidth)/2-1);
            using(var channels=_image.CountChannels())
            {
                if(channels.I==3)window.DispColor(_image);else window.DispImage(_image);
            }
            if(_region is not null&&OpacityPercent>0){var alpha=(int)Math.Clamp(OpacityPercent*2.55,0,255);window.SetColor($"#00e1ff{alpha:X2}");window.SetDraw("fill");window.DispRegion(_region);window.SetColor("cyan");window.SetDraw("margin");window.SetLineWidth(2);window.DispRegion(_region);}
            if(!string.IsNullOrWhiteSpace(Caption))window.DispText(Caption,"window",12,12,"cyan",new HTuple(),new HTuple());
            Message.Visibility=Visibility.Collapsed;LastDisplayError=null;
        }
        catch(Exception error) when(error is HalconException or IOException){LastDisplayError=error.Message;Message.Text="图像显示失败："+error.Message;Message.Visibility=Visibility.Visible;Release();}
    }
}
