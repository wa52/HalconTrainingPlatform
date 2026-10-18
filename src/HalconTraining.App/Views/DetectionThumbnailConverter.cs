using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HalconTraining.App.Services;
using HalconTraining.App.ViewModels;

namespace HalconTraining.App.Views;

/// <summary>Small, file-unlocked previews with the same review boxes as the full-size image.</summary>
public sealed class DetectionThumbnailConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not DetectionReviewRow row || values[1] is not int maxBoxes)
            return null;
        if (parameter is string label && label == "count")
            return $"{DetectionActiveLearningStore.ReviewBoxes(row.Image, maxBoxes).Count} 个框";
        try
        {
            using var stream = File.OpenRead(row.Image.ImagePath);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 128; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            var group = new DrawingGroup();
            group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
            group.Children.Add(new ImageDrawing(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight)));
            var pen = new Pen(row.Image.Status == "approved" ? Brushes.SeaGreen : Brushes.DodgerBlue, 1.5);
            foreach (var box in DetectionActiveLearningStore.ReviewBoxes(row.Image, maxBoxes))
            {
                var x1 = box.Col1 * bitmap.PixelWidth / row.Image.Width;
                var y1 = box.Row1 * bitmap.PixelHeight / row.Image.Height;
                var x2 = box.Col2 * bitmap.PixelWidth / row.Image.Width;
                var y2 = box.Row2 * bitmap.PixelHeight / row.Image.Height;
                if (!double.IsFinite(x1 + y1 + x2 + y2) || x2 <= x1 || y2 <= y1) continue;
                group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(x1, y1, x2 - x1, y2 - y1))));
            }
            var result = new DrawingImage(group); result.Freeze(); return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException)
        { return null; }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
