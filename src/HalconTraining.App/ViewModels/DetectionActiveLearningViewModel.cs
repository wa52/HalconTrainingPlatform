using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using HalconTraining.App.Services;

namespace HalconTraining.App.ViewModels;

public sealed class DetectionBoxEditor : ObservableModel
{
    private int _classId;
    private double _confidence, _col1, _row1, _col2, _row2;
    public DetectionBoxEditor(DetectionBox box)
    {
        _classId = box.ClassId; _confidence = box.Confidence;
        _col1 = box.Col1; _row1 = box.Row1; _col2 = box.Col2; _row2 = box.Row2;
    }
    public int ClassId { get => _classId; set => SetProperty(ref _classId, value); }
    public double Confidence => _confidence; // Model score, not an editable ground-truth attribute.
    public double Col1 { get => _col1; set => SetProperty(ref _col1, value); }
    public double Row1 { get => _row1; set => SetProperty(ref _row1, value); }
    public double Col2 { get => _col2; set => SetProperty(ref _col2, value); }
    public double Row2 { get => _row2; set => SetProperty(ref _row2, value); }
    public DetectionBox ToBox() => new(ClassId, Confidence, Col1, Row1, Col2, Row2);
}

public sealed class DetectionReviewRow(DetectionReviewImage image)
{
    public DetectionReviewImage Image { get; } = image;
    public string Name => Path.GetFileName(Image.ImagePath);
    public string Status => Image.Status switch { "approved" => "已确认", "rework" => "需重标", _ => "待审核" };
    public string Priority => Image.Priority.ToString("P0");
    public string Reason => Image.Reason;
    public int BoxCount => Image.PredictedBoxes.Count;
}

public sealed class DetectionActiveLearningViewModel : ObservableModel
{
    private readonly DetectionActiveLearningStore _store;
    private DetectionReviewSession? _session;
    private DetectionReviewRow? _selectedRow;
    private DetectionBoxEditor? _selectedBox;
    private bool _busy;
    private string _status = "导入目标检测预测JSON后，按不确定性审核检测框。";
    private string _lastExport = "";
    private IReadOnlyList<DetectionBox> _previewBoxes = [];

    public DetectionActiveLearningViewModel(string workspaceRoot)
    {
        _store = new DetectionActiveLearningStore(workspaceRoot);
        ImportCommand = new AsyncCommand(_ => ExecuteAsync(ImportAsync));
        ApproveCommand = new AsyncCommand(_ => ExecuteAsync(() => SaveReviewAsync("approved")));
        ReworkCommand = new AsyncCommand(_ => ExecuteAsync(() => SaveReviewAsync("rework")));
        SaveDraftCommand = new AsyncCommand(_ => ExecuteAsync(() => SaveReviewAsync("pending")));
        ExportCommand = new AsyncCommand(_ => ExecuteAsync(ExportAsync));
        AddBoxCommand = new RelayCommand(_ => AddBox());
        RemoveBoxCommand = new RelayCommand(_ => RemoveBox());
        NextCommand = new RelayCommand(_ => SelectNext());
        OpenExportCommand = new RelayCommand(_ =>
        {
            if (Directory.Exists(LastExport))
                Process.Start(new ProcessStartInfo(LastExport) { UseShellExecute = true });
        });
    }

    public AsyncCommand ImportCommand { get; }
    public AsyncCommand ApproveCommand { get; }
    public AsyncCommand ReworkCommand { get; }
    public AsyncCommand SaveDraftCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public RelayCommand AddBoxCommand { get; }
    public RelayCommand RemoveBoxCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand OpenExportCommand { get; }

    public ObservableCollection<DetectionReviewRow> Items { get; } = [];
    public ObservableCollection<DetectionBoxEditor> Boxes { get; } = [];
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string LastExport { get => _lastExport; private set => SetProperty(ref _lastExport, value); }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;
    public bool HasSession => _session is not null;
    public string ModelInfo => _session is null ? "尚未导入预测" : $"模型：{_session.ModelId} · {_session.Classes.Count} 类";
    public string ClassesInfo => _session is null ? "" : string.Join("    ", _session.Classes.Select(c => $"{c.Id} = {c.Name}"));
    public string ReviewProgress => _session is null ? "0 / 0" :
        $"{_session.Images.Count(i => i.Status == "approved")} 已确认 / {_session.Images.Count(i => i.Status == "pending")} 待审核 / {_session.Images.Count(i => i.Status == "rework")} 需重标";
    public DetectionReviewRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            Boxes.Clear();
            SelectedBox = null;
            if (value is not null)
            {
                var source = value.Image.HasDraft ? value.Image.ReviewedBoxes : value.Image.PredictedBoxes;
                foreach (var box in source)
                {
                    var editor = new DetectionBoxEditor(box);
                    editor.PropertyChanged += (_, _) => UpdatePreview();
                    Boxes.Add(editor);
                }
            }
            UpdatePreview();
            OnPropertyChanged(nameof(PreviewPath));
            OnPropertyChanged(nameof(SelectedDetails));
        }
    }
    public DetectionBoxEditor? SelectedBox { get => _selectedBox; set => SetProperty(ref _selectedBox, value); }
    public string? PreviewPath => SelectedRow?.Image.ImagePath;
    public IReadOnlyList<DetectionBox> PreviewBoxes { get => _previewBoxes; private set => SetProperty(ref _previewBoxes, value); }
    public string SelectedDetails => SelectedRow is null ? "选择待审核图片" :
        $"{SelectedRow.Image.Width} × {SelectedRow.Image.Height} · {SelectedRow.Status} · {SelectedRow.Reason}";

    public async Task InitializeAsync()
    {
        try
        {
            _session = await _store.LoadLatestAsync();
            if (_session is not null)
            {
                RefreshQueue();
                Status = "已恢复上次目标检测审核会话。";
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        { Status = "恢复审核记录失败：" + error.Message; }
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (Exception error) { Status = "操作失败：" + error.Message; }
        finally { IsBusy = false; }
    }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择目标检测预测结果JSON", Filter = "预测JSON|*.json", CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        var session = await _store.ImportAsync(dialog.FileName);
        _session = session;
        LastExport = "";
        RefreshQueue();
        Status = $"导入 {session.Images.Count} 张图片，已按预测不确定性排序；仅人工确认的结果可导出。";
    }

    private void RefreshQueue(string? selectPath = null)
    {
        if (_session is null) return;
        var old = selectPath ?? SelectedRow?.Image.ImagePath;
        var pending = DetectionActiveLearningStore.Pending(_session);
        var others = _session.Images.Where(i => i.Status != "pending")
            .OrderBy(i => i.Status).ThenByDescending(i => i.Priority);
        Items.Clear();
        foreach (var image in pending.Concat(others)) Items.Add(new DetectionReviewRow(image));
        SelectedRow = Items.FirstOrDefault(row => row.Image.ImagePath == old)
            ?? Items.FirstOrDefault(row => row.Image.Status == "pending")
            ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(ReviewProgress));
        OnPropertyChanged(nameof(ModelInfo));
        OnPropertyChanged(nameof(ClassesInfo));
        OnPropertyChanged(nameof(HasSession));
    }

    private void UpdatePreview() => PreviewBoxes = Boxes.Select(box => box.ToBox()).ToArray();

    private void AddBox()
    {
        if (IsBusy || SelectedRow is null || _session is null) return;
        var image = SelectedRow.Image;
        var firstId = _session.Classes[0].Id;
        var box = new DetectionBoxEditor(new DetectionBox(firstId, 0,
            image.Width * .25, image.Height * .25, image.Width * .75, image.Height * .75));
        box.PropertyChanged += (_, _) => UpdatePreview();
        Boxes.Add(box); SelectedBox = box;
        UpdatePreview();
        Status = "已添加手工框；请修改坐标和类别后点击「接受并下一张」。新增框的模型置信度为0，仅作记录。";
    }

    private void RemoveBox()
    {
        if (IsBusy || SelectedBox is null) return;
        Boxes.Remove(SelectedBox); SelectedBox = null; UpdatePreview();
    }

    private void SelectNext()
    {
        if (IsBusy) return;
        SelectedRow = Items.FirstOrDefault(row => row.Image.Status == "pending" && row != SelectedRow)
            ?? Items.FirstOrDefault(row => row != SelectedRow);
    }

    private async Task SaveReviewAsync(string newStatus)
    {
        if (_session is null || SelectedRow is null) return;
        var image = SelectedRow.Image;
        var boxes = Boxes.Select(box => box.ToBox()).ToList();
        // A rework flag or partial draft must be savable even while coordinates are incomplete.
        if (newStatus == "approved") DetectionActiveLearningStore.ValidateReviewedBoxes(image, boxes, _session.Classes);
        image.ReviewedBoxes = boxes;
        image.HasDraft = true;
        image.Status = newStatus;
        await _store.SaveAsync(_session);
        RefreshQueue(newStatus == "pending" ? image.ImagePath : null);
        if (newStatus != "pending") SelectedRow = Items.FirstOrDefault(row => row.Image.Status == "pending") ?? SelectedRow;
        Status = newStatus switch
        {
            "approved" => "人工确认已保存。该图片会进入COCO导出。",
            "rework" => "已标记需重标，不会导出成训练真值。",
            _ => "草稿已保存，尚未确认，不会导出。"
        };
    }

    private async Task ExportAsync()
    {
        if (_session is null) return;
        // The export is intentionally based on persisted, human-approved labels only.
        var folder = await Task.Run(() => _store.ExportApprovedCocoAsync(_session));
        LastExport = folder;
        Status = "已导出人工确认图片和COCO标注：" + folder;
    }
}
