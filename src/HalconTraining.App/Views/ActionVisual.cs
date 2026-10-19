using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace HalconTraining.App.Views;

/// <summary>One presentation policy for XAML, generated controls and owned windows.</summary>
public static class ActionVisual
{
    public const int HoverDelay=1500;
    public static readonly DependencyProperty KindProperty=DependencyProperty.RegisterAttached("Kind",typeof(string),typeof(ActionVisual),new PropertyMetadata(null,KindChanged));
    public static readonly DependencyProperty LabelProperty=DependencyProperty.RegisterAttached("Label",typeof(object),typeof(ActionVisual));
    public static readonly DependencyProperty HelpProperty=DependencyProperty.RegisterAttached("Help",typeof(object),typeof(ActionVisual));
    public static readonly DependencyProperty KeepTextProperty=DependencyProperty.RegisterAttached("KeepText",typeof(bool),typeof(ActionVisual),new PropertyMetadata(false));
    public static readonly DependencyProperty DataItemProperty=DependencyProperty.RegisterAttached("DataItem",typeof(bool),typeof(ActionVisual),new PropertyMetadata(false));
    public static readonly DependencyProperty ShortcutProperty=DependencyProperty.RegisterAttached("Shortcut",typeof(string),typeof(ActionVisual),new PropertyMetadata(""));
    public static readonly DependencyProperty IsSelectedProperty=DependencyProperty.RegisterAttached("IsSelected",typeof(bool),typeof(ActionVisual),new PropertyMetadata(false));
    private static readonly DependencyProperty PreparedProperty=DependencyProperty.RegisterAttached("Prepared",typeof(bool),typeof(ActionVisual),new PropertyMetadata(false));
    public static string? GetKind(DependencyObject element)=>(string?)element.GetValue(KindProperty);
    public static void SetKind(DependencyObject element,string value)=>element.SetValue(KindProperty,value);
    public static object? GetLabel(DependencyObject element)=>element.GetValue(LabelProperty);
    public static object? GetHelp(DependencyObject element)=>element.GetValue(HelpProperty);
    public static bool GetKeepText(DependencyObject element)=>(bool)element.GetValue(KeepTextProperty);
    public static void SetKeepText(DependencyObject element,bool value)=>element.SetValue(KeepTextProperty,value);
    public static bool GetDataItem(DependencyObject element)=>(bool)element.GetValue(DataItemProperty);
    public static void SetDataItem(DependencyObject element,bool value)=>element.SetValue(DataItemProperty,value);
    public static string GetShortcut(DependencyObject element)=>(string)element.GetValue(ShortcutProperty);
    public static void SetShortcut(DependencyObject element,string value)=>element.SetValue(ShortcutProperty,value);
    public static bool GetIsSelected(DependencyObject element)=>(bool)element.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject element,bool value)=>element.SetValue(IsSelectedProperty,value);
    public static void InitializeToolTips()
    {
        // BetweenShowDelay=0 prevents the instant-tooltip shortcut on adjacent controls.
        foreach(var type in new[]{typeof(FrameworkElement),typeof(FrameworkContentElement)})
        {
            ToolTipService.InitialShowDelayProperty.OverrideMetadata(type,new FrameworkPropertyMetadata(HoverDelay));
            ToolTipService.BetweenShowDelayProperty.OverrideMetadata(type,new FrameworkPropertyMetadata(0));
            ToolTipService.ShowDurationProperty.OverrideMetadata(type,new FrameworkPropertyMetadata(10000));
            ToolTipService.ShowOnDisabledProperty.OverrideMetadata(type,new FrameworkPropertyMetadata(true));
        }
        EventManager.RegisterClassHandler(typeof(DataGrid),FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,_)=>
        {
            foreach(var column in ((DataGrid)sender).Columns.OfType<DataGridTextColumn>())
            {
                if(column.ElementStyle?.Setters.OfType<Setter>().Any(setter=>setter.Property==FrameworkElement.ToolTipProperty)==true)continue;
                var style=new Style(typeof(TextBlock),column.ElementStyle);
                style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
                style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding("Text"){RelativeSource=RelativeSource.Self}));
                column.ElementStyle=style;
            }
        }));
    }
    private static void KindChanged(DependencyObject sender,DependencyPropertyChangedEventArgs args)
    {
        if(sender is not FrameworkElement element)return;
        if(!ActionIcon.Exists((string)args.NewValue))throw new ArgumentException("未知操作图标："+args.NewValue);
        element.Loaded+=Loaded;
        if(element.IsLoaded)Prepare(element);
    }
    private static void Loaded(object sender,RoutedEventArgs args)=>Prepare((FrameworkElement)sender);
    private static void Prepare(FrameworkElement element)
    {
        element.Loaded-=Loaded;
        if((bool)element.GetValue(PreparedProperty))return;
        element.SetValue(PreparedProperty,true);
        var header=element is HeaderedContentControl;
        BindingOperations.SetBinding(element,LabelProperty,new Binding(header?"Header":"Content"){Source=element});
        BindingOperations.SetBinding(element,AutomationProperties.NameProperty,new Binding{Source=element,Path=new PropertyPath(LabelProperty)});
        var original=BindingOperations.GetBindingBase(element,FrameworkElement.ToolTipProperty);
        if(original is not null)BindingOperations.SetBinding(element,HelpProperty,original);
        else element.SetValue(HelpProperty,element.ToolTip);
        var help=new MultiBinding{Converter=new ActionHelpConverter()};
        help.Bindings.Add(new Binding{Source=element,Path=new PropertyPath(LabelProperty)});
        help.Bindings.Add(new Binding{Source=element,Path=new PropertyPath(HelpProperty)});
        help.Bindings.Add(new Binding{Source=element,Path=new PropertyPath(ShortcutProperty)});
        BindingOperations.SetBinding(element,FrameworkElement.ToolTipProperty,help);
        BindingOperations.SetBinding(element,AutomationProperties.HelpTextProperty,help);
        BindingOperations.SetBinding(element,AutomationProperties.AcceleratorKeyProperty,new Binding{Source=element,Path=new PropertyPath(ShortcutProperty)});
        var key=element switch{Expander=>"IconDataHeaderTemplate",TabItem=>"IconTabTemplate",_=>GetKeepText(element)?"IconTextActionTemplate":"IconActionTemplate"};
        if(header)((HeaderedContentControl)element).HeaderTemplate=(DataTemplate)element.FindResource(key);
        else if(element is ContentControl content)content.ContentTemplate=(DataTemplate)element.FindResource(key);
        if(element is Button button)
        {
            button.SetCurrentValue(Control.PaddingProperty,new Thickness(8));
            button.SetCurrentValue(FrameworkElement.MinWidthProperty,36.0);
            button.SetCurrentValue(FrameworkElement.MinHeightProperty,36.0);
        }
        if(element is Expander expander)
        {
            expander.ApplyTemplate();
            if(expander.Template.FindName("HeaderSite",expander) is ToggleButton toggle)
            {
                // The hint belongs to the collapse/expand action, not all parameter inputs inside it.
                BindingOperations.SetBinding(toggle,FrameworkElement.ToolTipProperty,help);
                BindingOperations.SetBinding(toggle,AutomationProperties.NameProperty,new Binding{Source=element,Path=new PropertyPath(LabelProperty)});
                element.ClearValue(FrameworkElement.ToolTipProperty);
            }
        }
    }
}

public sealed class ActionHelpConverter:IMultiValueConverter
{
    public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture)
    {
        var label=values[0]?.ToString()??"";
        var detail=values[1] is null || ReferenceEquals(values[1],DependencyProperty.UnsetValue) ? "" : values[1].ToString();
        var shortcut=values[2]?.ToString()??"";
        return label+(string.IsNullOrWhiteSpace(shortcut)?"":$"（{shortcut}）")+
            (string.IsNullOrWhiteSpace(detail)||detail==label?"":"\n"+detail);
    }
    public object[] ConvertBack(object value,Type[] targetTypes,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
