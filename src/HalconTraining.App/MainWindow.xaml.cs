using System.Windows;
using System.Windows.Controls;
using HalconTraining.App.ViewModels;

namespace HalconTraining.App;

public partial class MainWindow : Window
{
    private bool _closing;
    public MainWindow(DesktopWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var errors=new HashSet<ValidationError>();
        EventHandler<ValidationErrorEventArgs> trackParameterErrors=(_,event_)=>
        {
            if(event_.Action==ValidationErrorEventAction.Added)errors.Add(event_.Error);else errors.Remove(event_.Error);
            viewModel.HasInputErrors=errors.Count>0;
        };
        Validation.AddErrorHandler(CoreParameters,trackParameterErrors);
        Validation.AddErrorHandler(AdvancedParameters,trackParameterErrors);
        Closing+=async(_,e)=>
        {
            if(_closing)return;
            if(!viewModel.HasActiveOperations)return;
            e.Cancel=true;_closing=true;
            await viewModel.StopAndWaitAsync();Close();
        };
        Closed+=(_,_)=>viewModel.Dispose();
    }
}
