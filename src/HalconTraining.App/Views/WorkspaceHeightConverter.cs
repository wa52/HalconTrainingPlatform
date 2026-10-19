using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HalconTraining.App.Views;

/// <summary>Keep a usable image viewport; expanded tools grow the scrollable workspace.</summary>
public sealed class WorkspaceHeightConverter:IMultiValueConverter
{
    public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture)
    {
        var viewport=values[0] is double height&&double.IsFinite(height)?height:560;
        var minimum=560.0;
        foreach(var value in values.Skip(1))if(value is true)minimum+=150;
        return Math.Max(minimum,viewport-12);
    }
    public object[] ConvertBack(object value,Type[] targetTypes,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
