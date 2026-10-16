using System.Windows;
using HalconTraining.App.ViewModels;

namespace HalconTraining.App;

public partial class MainWindow : Window
{
    private bool _closing;
    public MainWindow(DesktopWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing+=async(_,e)=>
        {
            if(_closing)return;
            if(!viewModel.IsBusy)return;
            e.Cancel=true;_closing=true;
            await viewModel.StopAndWaitAsync();Close();
        };
        Closed+=(_,_)=>viewModel.Dispose();
    }
}
