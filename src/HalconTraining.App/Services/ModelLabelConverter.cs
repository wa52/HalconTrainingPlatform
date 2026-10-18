using System.Globalization;
using System.Windows.Data;
namespace HalconTraining.App.Services;
public sealed class ModelLabelConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value?.ToString() switch
    {
        "reload_and_evaluation_passed"=>"已验证，可测试","trained_pending_validation"=>"训练完成，待评估",
        "bootstrap_unvalidated"=>"训练完成，待精度评估",
        "failed"=>"失败","interrupted"=>"已中断","invalid"=>"文件异常","unknown"=>"状态未知",
        "semantic_segmentation"=>"语义分割","classification"=>"图像分类","detection"=>"目标检测",_=>parameter?.ToString()=="run"&&value is string directory?System.IO.Path.GetFileName(directory):value??"—"
    };
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
