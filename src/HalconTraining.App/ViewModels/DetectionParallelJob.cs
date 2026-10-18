using System.Text.Json;
using HalconTraining.App.Services;

namespace HalconTraining.App.ViewModels;

public sealed class DetectionParallelJob(int classId, string className, int epochs) : ObservableModel
{
    private string _state = "排队中", _detail = "等待训练名额";
    private int _epoch;
    private double? _loss;
    public int ClassId { get; } = classId;
    public string ClassName { get; } = className;
    public int Epochs { get; } = epochs;
    public string State { get => _state; set { if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(IsFinished)); } }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public int Epoch { get => _epoch; private set { if (SetProperty(ref _epoch, value)) OnPropertyChanged(nameof(EpochText)); } }
    public string EpochText => $"{Epoch} / {Epochs}";
    public double? Loss { get => _loss; private set => SetProperty(ref _loss, value); }
    public string ElapsedText => StartedAt is not { } start ? "—" : ((EndedAt ?? DateTimeOffset.UtcNow) - start).ToString(@"mm\:ss");
    public string OutputDirectory { get; set; } = "";
    public int? ProcessId { get; private set; }
    public DateTimeOffset? ProcessReportedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public bool IsFinished => State is "已完成" or "失败" or "已停止";
    public void RefreshElapsed() => OnPropertyChanged(nameof(ElapsedText));
    public void Receive(string line)
    {
        if (IsFinished) return;
        var message = DetectionProgressMessage.Parse(line);
        if (message is null) return;
        Detail = message.Stage;
        if (message.Current is { } epoch) { Epoch = epoch; State = "训练中"; }
        if (message.Loss is { } loss) Loss = loss;
        try
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("pid", out var pid)) { ProcessId = pid.GetInt32(); ProcessReportedAt = DateTimeOffset.UtcNow; }
        }
        catch (JsonException) { }
    }
}
