using Microsoft.Win32;

namespace HalconTraining.App.Services;

public interface IWorkspaceDialogs
{
    string? Dataset();
    string[] Images();
}
public sealed class WorkspaceDialogs : IWorkspaceDialogs
{
    public string? Dataset()
    {
        var dialog=new OpenFileDialog {Title="选择HALCON数据集",Filter="HALCON数据集|*.hdict;*.json",CheckFileExists=true};
        return dialog.ShowDialog()==true?dialog.FileName:null;
    }
    public string[] Images()
    {
        var dialog=new OpenFileDialog {Title="选择原图（可多选）",Filter="原图|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",Multiselect=true,CheckFileExists=true};
        return dialog.ShowDialog()==true?dialog.FileNames:[];
    }
}
