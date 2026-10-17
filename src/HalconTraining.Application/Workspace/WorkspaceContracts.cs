using System.Text.Json;

namespace HalconTraining.Application.Workspace;

public sealed record DatasetSample(string Name, string Path, bool Missing, string Split, string? MaskPath = null, string? Label = null);
public sealed record DatasetReport(string Task, int ImageCount, int ClassCount, int MaskCount, int AnnotationCount,
    Dictionary<string, int> SplitCounts, string[] Classes, DatasetSample[] Samples, string[] Issues);
public sealed record TrainingSettings(string Dataset, string ModelFile, int Epochs = 60, int Batch = 2, int Width = 512,
    int Height = 512, double Rate = .001, int Seed = 42, double TrainPercent = 70, double ValidationPercent = 15, int ValidationInterval = 1, string Task = "semantic_segmentation");
public sealed record TrainingProgress(string Stage, string Detail, int Epoch = 0, int Epochs = 0, int Batch = 0, int Batches = 0, double? Loss = null,
    double? MeanLoss = null, double? ValidationIoU = null, double? LearningRate = null, double? IterationMs = null,
    string? PreviewImage = null, string? PreviewMask = null, string? TruthMask = null, double? EpochSeconds = null, double? ValidationAccuracy = null, string? TruthLabel = null, string? PredictedLabel = null, double? Confidence = null);
public sealed record ModelEntry(string Directory, string Name, string Task, string Status, string Metrics)
{
    public bool CanTest => (Task is "semantic_segmentation" or "classification") && Status == "reload_and_evaluation_passed";
}
public sealed record PredictionArea(int ClassId, double Pixels, double Ratio);
public sealed record ClassificationScore(int ClassId, string ClassName, double Confidence);
public sealed record PredictionResult(string File, int Width, int Height, double InferenceMs, double TotalMs,
    string OutputDirectory, PredictionArea[] Areas, string Task = "semantic_segmentation", string? ClassName = null, double? Confidence = null, ClassificationScore[]? Scores = null)
{
    public string OriginalPath => System.IO.Path.Combine(OutputDirectory, "original.png");
    public string? MaskPath => Task == "classification" ? null : System.IO.Path.Combine(OutputDirectory, "mask.png");
}
public sealed record DesktopSession(string? Dataset, TrainingSettings? Settings, string? SelectedModel);
public sealed record WorkerOutput(int ExitCode, string Output, string Error);
public interface IHalconProcessRunner
{
    Task<WorkerOutput> RunAsync(IReadOnlyList<string> arguments, IProgress<string>? output, CancellationToken cancellationToken);
}
public interface IWorkspaceStore
{
    string Root { get; }
    string NewRun(string dataset, string task = "semantic_segmentation");
    string NewTest(string modelDirectory);
    Task SaveStateAsync(string directory, string status, string? error = null);
    Task PrepareAsync(string directory, TrainingSettings settings);
    Task CompleteAsync(string directory);
    Task ValidateModelAsync(string directory);
    Task<IReadOnlyList<ModelEntry>> ModelsAsync();
    Task<IReadOnlyList<PredictionResult>> ResultsAsync(string modelDirectory);
    Task SaveSessionAsync(DesktopSession session);
    Task<DesktopSession?> LoadSessionAsync();
}
public static class WorkspaceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
