using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HalconTraining.App.ViewModels;

namespace HalconTraining.App;

public partial class MainWindow : Window
{
    private bool _closing;
    public MainWindow(DesktopWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        PreviewKeyDown+=(_,args)=>
        {
            if(Keyboard.Modifiers==ModifierKeys.Control && args.Key is Key.D1 or Key.D2 or Key.D3)
            {
                viewModel.SelectedPageIndex=(int)args.Key-(int)Key.D1;args.Handled=true;return;
            }
            if(viewModel.HasActiveOperations)return;
            if(Keyboard.Modifiers==(ModifierKeys.Control|ModifierKeys.Shift)&&args.Key==Key.O)
            {
                viewModel.SelectedPageIndex=DesktopWorkspaceViewModel.DatasetPage;DatasetPages.SelectedIndex=1;
                viewModel.ActiveLearning.ImportFolderCommand.Execute(null);args.Handled=true;
            }
            else if(Keyboard.Modifiers==ModifierKeys.Control&&args.Key==Key.O)
            {
                viewModel.SelectedPageIndex=DesktopWorkspaceViewModel.DatasetPage;DatasetPages.SelectedIndex=0;
                viewModel.BrowseDatasetCommand.Execute(null);args.Handled=true;
            }
            else if(Keyboard.Modifiers==ModifierKeys.Control&&args.Key==Key.S)
            {
                if(DatasetPages.SelectedIndex==1&&viewModel.SelectedPageIndex==DesktopWorkspaceViewModel.DatasetPage)
                {if(viewModel.ActiveLearning.CanReview)viewModel.ActiveLearning.SaveDraftCommand.Execute(null);}
                else if(!viewModel.HasInputErrors&&viewModel.HasDataset)viewModel.SaveParametersCommand.Execute(null);
                args.Handled=true;
            }
        };
        DatasetPages.SelectionChanged+=(_,args)=>
        {
            if(ReferenceEquals(args.Source,DatasetPages))viewModel.DetectionSelected=DatasetPages.SelectedIndex==1;
        };
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
