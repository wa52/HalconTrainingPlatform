using System.Collections.ObjectModel;
using HalconTraining.Application.Environment;
using HalconTraining.Application.Workers;

namespace HalconTraining.App.ViewModels;

public sealed class ShellViewModel : ObservableModel
{
    private readonly ITrainingWorkerClient _workerClient;
    private WorkspacePageViewModel _selectedPage;
    private string _environmentSummary = "尚未检查本机环境";
    private string _lastError = string.Empty;
    private bool _isProbing;

    public ShellViewModel(ITrainingWorkerClient workerClient)
    {
        _workerClient = workerClient ?? throw new ArgumentNullException(nameof(workerClient));
        Pages = new ObservableCollection<WorkspacePageViewModel>
        {
            new ProjectOverviewViewModel(),
            new DatasetViewModel(),
            new AnnotationViewModel(),
            new ExperimentViewModel(),
            new TrainingMonitorViewModel(),
            new EvaluationViewModel(),
            new ModelLibraryViewModel()
        };
        _selectedPage = Pages[0];
        _selectedPage.IsSelected = true;
        NavigateCommand = new RelayCommand(parameter => SelectPage(parameter as string));
        ProbeEnvironmentCommand = new AsyncCommand(ProbeEnvironmentAsync);
    }

    public ObservableCollection<WorkspacePageViewModel> Pages { get; }
    public AsyncCommand ProbeEnvironmentCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public WorkspacePageViewModel SelectedPage { get => _selectedPage; private set => SetProperty(ref _selectedPage, value); }
    public string EnvironmentSummary { get => _environmentSummary; private set => SetProperty(ref _environmentSummary, value); }
    public string LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }
    public bool IsProbing { get => _isProbing; private set => SetProperty(ref _isProbing, value); }

    public void SelectPage(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return;
        var page = Pages.FirstOrDefault(item => item.Id == pageId);
        if (page is null) return;
        SelectedPage.IsSelected = false;
        SelectedPage = page;
        SelectedPage.IsSelected = true;
    }

    private async Task ProbeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (IsProbing) return;
        IsProbing = true;
        LastError = string.Empty;
        EnvironmentSummary = "正在读取 HALCON 与 GPU 环境…";
        try
        {
            var report = await _workerClient.CaptureEnvironmentAsync(cancellationToken);
            LastReport = report;
            var passed = report.Checks.Count(check => check.Status == Domain.Training.CapabilityStatus.Passed);
            var concerns = report.Checks.Count(check => check.Status is Domain.Training.CapabilityStatus.Unsupported or Domain.Training.CapabilityStatus.Failed);
            EnvironmentSummary = $"{report.HalconVersion ?? "HALCON 未识别"} · {report.GpuName ?? "GPU 未识别"} · {passed} 项通过，{concerns} 项需处理";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EnvironmentSummary = "环境检查已取消";
        }
        catch (Exception exception)
        {
            LastError = $"环境检查失败：{exception.Message}";
            EnvironmentSummary = "环境检查未完成";
        }
        finally
        {
            IsProbing = false;
        }
    }

    private TrainingEnvironmentReport? _lastReport;
    public TrainingEnvironmentReport? LastReport { get => _lastReport; private set => SetProperty(ref _lastReport, value); }
}
