using System.Windows;
using HalconTraining.App.ViewModels;
using HalconTraining.App.Services;
using System.IO;
using HalconTraining.Application.Workspace;
using HalconTraining.Persistence.Workspace;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HalconTraining.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private bool _ownsInstance;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr handle,int command);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var root=FindWorkspace();
        var identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..16];
        _instance=new Mutex(true,"Local\\HalconTraining-"+identity,out _ownsInstance);
        if(!_ownsInstance)
        {
            foreach(var process in Process.GetProcessesByName("HalconTraining.App"))
            {
                using(process)if(process.Id!=System.Environment.ProcessId&&process.MainWindowHandle!=IntPtr.Zero){ShowWindowAsync(process.MainWindowHandle,9);SetForegroundWindow(process.MainWindowHandle);break;}
            }
            Shutdown(0);return;
        }
        DispatcherUnhandledException+=(_,error)=>
        {
            Directory.CreateDirectory(Path.Combine(root,"logs"));
            File.AppendAllText(Path.Combine(root,"logs","desktop-error.log"),DateTimeOffset.Now+" "+error.Exception+System.Environment.NewLine);
            error.Handled=true;
            if(!e.Args.Contains("--validate-desktop"))MessageBox.Show("程序遇到错误，详情已保存到logs/desktop-error.log。","HALCON训练平台",MessageBoxButton.OK,MessageBoxImage.Error);
            Shutdown(1);
        };
        var viewModel=new DesktopWorkspaceViewModel(new TrainingWorkspace(new HalconProcessRunner(),new FileWorkspaceStore(root)),new WorkspaceDialogs(),new NamedPipeTrainingWorkerClient());
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        window.Loaded+=async(_,_)=>
        {
            await viewModel.InitializeAsync();
            if(e.Args.Contains("--validate-desktop"))
            {
                var result=await DesktopValidation.RunAsync(window,viewModel,e.Args);Shutdown(result);
            }
        };
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if(_ownsInstance)_instance?.ReleaseMutex();_instance?.Dispose();base.OnExit(e);
    }
    private static string FindWorkspace()
    {
        var configured=Environment.GetEnvironmentVariable("HALCON_TRAINING_HOME");
        if(configured is not null&&Directory.Exists(configured))return configured;
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"assets","pretrained","catalog.json")))return directory.FullName;
        throw new DirectoryNotFoundException("找不到训练平台目录，可通过HALCON_TRAINING_HOME指定。");
    }
}
