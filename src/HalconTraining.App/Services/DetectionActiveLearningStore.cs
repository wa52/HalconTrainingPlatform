using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HalconTraining.App.Services;

/// <summary>
/// Contract for model-independent rectangle1 detection predictions. Coordinates are original-image pixels.
/// Prediction JSON is never treated as verified ground truth.
/// </summary>
public sealed record DetectionCategory(int Id, string Name);
public sealed record DetectionBox(int ClassId, double Confidence, double Col1, double Row1, double Col2, double Row2,
    double? RunnerUpConfidence = null);
public sealed record DetectionPredictionSummary(int Processed, int DetectedImages, int EmptyImages, int TotalBoxes, int Failed)
{
    public string Description => $"已处理 {Processed:N0} 张 · 有框 {DetectedImages:N0} 张（{TotalBoxes:N0} 个框） · 未检出 {EmptyImages:N0} 张 · 失败 {Failed:N0} 张";
    public static DetectionPredictionSummary From(IReadOnlyList<DetectionReviewImage> images, int failed) =>
        new(images.Count + failed, images.Count(image => image.PredictedBoxes.Count > 0),
            images.Count(image => image.PredictedBoxes.Count == 0), images.Sum(image => image.PredictedBoxes.Count), failed);
}
public sealed class DetectionPredictionImage
{
    public string ImagePath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string? GroupId { get; set; }
    public List<DetectionBox> Boxes { get; set; } = [];
}
public sealed class DetectionPredictionFile
{
    public int SchemaVersion { get; set; } = 1;
    public string ModelId { get; set; } = "";
    public List<DetectionCategory> Classes { get; set; } = [];
    public List<DetectionPredictionImage> Images { get; set; } = [];
}
public sealed class DetectionReviewImage
{
    public string ImagePath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string? GroupId { get; set; }
    public int? FolderClassId { get; set; }
    public List<DetectionBox> PredictedBoxes { get; set; } = [];
    public List<DetectionBox> ReviewedBoxes { get; set; } = [];
    public bool HasDraft { get; set; }
    public string Status { get; set; } = "pending";
    public double Priority { get; set; }
    public string Reason { get; set; } = "";
}
public sealed class DetectionReviewSession
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string SourcePredictionFile { get; set; } = "";
    public string DatasetName { get; set; } = "";
    public string? SourceDirectory { get; set; }
    public List<string> ImportIssues { get; set; } = [];
    public string? LastTrainedModelPath { get; set; }
    public string? CombinedModelPath { get; set; }
    public Dictionary<int, string> FolderModels { get; set; } = [];
    public int? SelectedFolderClassId { get; set; }
    public int? PredictionMaxBoxes { get; set; }
    public double PredictionConfidence { get; set; } = .05;
    public Dictionary<int, double> FolderPredictionConfidences { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<DetectionCategory> Classes { get; set; } = [];
    public List<DetectionReviewImage> Images { get; set; } = [];
}

/// <summary>
/// Validates predictions, ranks an active-learning review queue, saves reviews atomically,
/// and exports only human-approved boxes to COCO. Does not modify original images/datasets.
/// </summary>
public sealed partial class DetectionActiveLearningStore(string workspaceRoot)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _sessions = Path.Combine(workspaceRoot, "active-learning", "sessions");
    private readonly string _exports = Path.Combine(workspaceRoot, "active-learning", "exports");

    /// <summary>Limit model candidates only; manual decisions are never reduced by inference settings.</summary>
    public static IReadOnlyList<DetectionBox> ReviewBoxes(DetectionReviewImage image, int maxPredictions)
    {
        if (maxPredictions is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(maxPredictions));
        if (image.HasDraft || image.Status == "approved") return image.ReviewedBoxes;
        return image.PredictedBoxes.OrderByDescending(box => box.Confidence).Take(maxPredictions).ToArray();
    }

    /// <summary>Only valid positive pending reviews in the requested folder can be approved in bulk.</summary>
    public static IReadOnlyList<DetectionReviewImage> BulkApprovalCandidates(DetectionReviewSession session,
        int? folderClassId, int maxPredictions)
    {
        if (session.SourceDirectory is not null && folderClassId is null)
            throw new InvalidOperationException("请选择一个类别文件夹，禁止跨类别全部确认。");
        var categories = folderClassId is { } id
            ? session.Classes.Where(category => category.Id == id).ToArray() : session.Classes.ToArray();
        var candidates = new List<DetectionReviewImage>();
        foreach (var image in session.Images.Where(image => image.Status == "pending" &&
            (folderClassId is null || image.FolderClassId == folderClassId)))
        {
            var boxes = ReviewBoxes(image, maxPredictions);
            if (boxes.Count == 0 || !File.Exists(image.ImagePath)) continue;
            try { ValidateReviewedBoxes(image, boxes, categories); }
            catch (InvalidDataException) { continue; }
            candidates.Add(image);
        }
        return candidates;
    }

    public async Task<(DetectionReviewSession Session, int Count, string BackupFile)> ApproveAllAsync(
        DetectionReviewSession session, int? folderClassId, int maxPredictions)
    {
        var next = JsonSerializer.Deserialize<DetectionReviewSession>(JsonSerializer.Serialize(session, Json), Json)!;
        var candidates = BulkApprovalCandidates(next, folderClassId, maxPredictions);
        if (candidates.Count == 0) return (session, 0, "");
        foreach (var image in candidates)
        {
            image.ReviewedBoxes = ReviewBoxes(image, maxPredictions).ToList();
            image.HasDraft = true;
            image.Status = "approved";
            image.Reason = "用户批量确认";
        }
        // Preserve the exact pre-confirmation state before the atomic session replacement.
        var backupFolder = Path.Combine(workspaceRoot, "active-learning", "review-backups");
        Directory.CreateDirectory(backupFolder);
        var backup = Path.Combine(backupFolder, DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss") +
            "_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        await File.WriteAllTextAsync(backup, JsonSerializer.Serialize(session, Json));
        await SaveAsync(next);
        return (next, candidates.Count, backup);
    }

    public static (double Priority, string Reason) Rank(IReadOnlyList<DetectionBox> boxes)
    {
        if (boxes.Count == 0)
            return (0.75, "未检测到目标：请确认真阴性或漏检");
        var weakest = boxes.OrderBy(box => box.Confidence).First();
        var margin = boxes.Where(box => box.RunnerUpConfidence.HasValue)
            .OrderBy(box => box.Confidence - box.RunnerUpConfidence!.Value).FirstOrDefault();
        var confidenceUncertainty = 1 - weakest.Confidence;
        var marginUncertainty = margin is null ? 0 : 1 - (margin.Confidence - margin.RunnerUpConfidence!.Value);
        return marginUncertainty > confidenceUncertainty
            ? (marginUncertainty, "类别得分接近，需要复核类别")
            : (confidenceUncertainty, $"低置信度检测框：{weakest.Confidence:P0}");
    }

    public static IReadOnlyList<DetectionReviewImage> Pending(DetectionReviewSession session)
    {
        // One high-priority image from each workpiece/batch per round prevents one batch
        // from consuming the entire review budget. Without groupId, images stay independent.
        var groups = session.Images.Where(image => image.Status == "pending")
            .GroupBy(image => string.IsNullOrWhiteSpace(image.GroupId) ? image.ImagePath : image.GroupId.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new Queue<DetectionReviewImage>(group.OrderByDescending(image => image.Priority)
                .ThenBy(image => image.ImagePath, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(group => group.Peek().Priority).ToArray();
        var queue = new List<DetectionReviewImage>();
        var remaining = groups.Sum(group => group.Count);
        while (remaining > 0)
        {
            foreach (var group in groups)
                if (group.Count > 0) { queue.Add(group.Dequeue()); remaining--; }
        }
        return queue;
    }

    private static void ValidateBox(DetectionBox box, int width, int height, HashSet<int> ids)
    {
        if (!ids.Contains(box.ClassId)) throw new InvalidDataException($"检测框类别ID {box.ClassId} 未在类别表中定义。");
        if (!double.IsFinite(box.Confidence) || box.Confidence < 0 || box.Confidence > 1 ||
            box.RunnerUpConfidence is { } alt && (!double.IsFinite(alt) || alt < 0 || alt > box.Confidence))
            throw new InvalidDataException("置信度必须在0到1之间，第二候选不能高于第一候选。");
        if (!double.IsFinite(box.Col1) || !double.IsFinite(box.Row1) ||
            !double.IsFinite(box.Col2) || !double.IsFinite(box.Row2) ||
            box.Col1 < 0 || box.Row1 < 0 || box.Col2 > width || box.Row2 > height ||
            box.Col2 <= box.Col1 || box.Row2 <= box.Row1)
            throw new InvalidDataException("检测框越界或宽高无效（使用原图像素坐标）。");
    }

    public static void ValidateReviewedBoxes(DetectionReviewImage image, IReadOnlyList<DetectionBox> boxes, IReadOnlyList<DetectionCategory> classes)
    {
        var ids = classes.Select(c => c.Id).ToHashSet();
        foreach (var box in boxes) ValidateBox(box, image.Width, image.Height, ids);
    }

    /// <summary>Start a manual seed round; no predictions are required.</summary>
    public async Task<DetectionReviewSession> CreateSeedAsync(
        IReadOnlyList<(string Path, int Width, int Height)> images, IReadOnlyList<string> classNames)
    {
        if (images.Count == 0) throw new InvalidDataException("请先选择至少一张原图。");
        if (classNames.Count == 0 || classNames.Any(string.IsNullOrWhiteSpace) ||
            classNames.Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != classNames.Count)
            throw new InvalidDataException("需要提供不重复的目标类别名称。");
        if (classNames.Any(name => name.Contains('/') || name.Contains('\\')))
            throw new InvalidDataException("类别名称不能包含路径分隔符。");
        var session = new DetectionReviewSession
        {
            Id = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N")[..8],
            ModelId = "manual-seed", SourcePredictionFile = "", CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Classes = classNames.Select((name, index) => new DetectionCategory(index + 1, name.Trim())).ToList()
        };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in images)
        {
            var full = Path.GetFullPath(item.Path);
            if (!File.Exists(full)) throw new FileNotFoundException("原始图片不存在。", full);
            if (item.Width <= 0 || item.Height <= 0 || !paths.Add(full))
                throw new InvalidDataException("图片尺寸无效或包含重复图片：" + full);
            session.Images.Add(new DetectionReviewImage
            {
                ImagePath = full, Width = item.Width, Height = item.Height,
                Priority = 1, Reason = "手工初始标注 · 尚无预测", Status = "pending"
            });
        }
        await SaveAsync(session);
        return session;
    }

    /// <summary>Add more images without replacing existing confirmed annotations or classes.</summary>
    public static bool ReadyForBootstrap(DetectionReviewSession session)
    {
        var confirmed = session.Images.Where(image => image.Status == "approved").ToArray();
        return confirmed.Length >= 5 && session.Classes.Count > 0 &&
            session.Classes.All(category => confirmed.SelectMany(image => image.ReviewedBoxes)
                .Any(box => box.ClassId == category.Id));
    }

    public async Task<int> AppendSeedImagesAsync(DetectionReviewSession session,
        IReadOnlyList<(string Path, int Width, int Height)> images)
    {
        if (session.Classes.Count == 0) throw new InvalidDataException("请先创建或导入类别。");
        var existing = session.Images.Select(i => Path.GetFullPath(i.ImagePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = new List<DetectionReviewImage>();
        foreach (var item in images)
        {
            var full = Path.GetFullPath(item.Path);
            if (!File.Exists(full)) throw new FileNotFoundException("原始图片不存在。", full);
            if (item.Width <= 0 || item.Height <= 0)
                throw new InvalidDataException("原图尺寸无效：" + full);
            if (!existing.Add(full)) continue;
            additions.Add(new DetectionReviewImage
            {
                ImagePath = full, Width = item.Width, Height = item.Height,
                Priority = 1, Reason = "新增人工标注 · 尚无预测", Status = "pending"
            });
        }
        session.Images.AddRange(additions);
        if (additions.Count > 0) await SaveAsync(session);
        return additions.Count;
    }

    public async Task<DetectionReviewSession> ImportAsync(string filename, bool persist = true)
    {
        var full = Path.GetFullPath(filename);
        var input = JsonSerializer.Deserialize<DetectionPredictionFile>(await File.ReadAllTextAsync(full), Json)
            ?? throw new InvalidDataException("预测文件为空。");
        if (input.SchemaVersion != 1 || string.IsNullOrWhiteSpace(input.ModelId))
            throw new InvalidDataException("仅支持schemaVersion=1，且必须注明modelId。");
        if (input.Classes is null || input.Classes.Count == 0 ||
            input.Classes.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Id < 0) ||
            input.Classes.Select(c => c.Id).Distinct().Count() != input.Classes.Count)
            throw new InvalidDataException("类别表不能为空，ID必须唯一且非负。");
        if (input.Images is null || input.Images.Count == 0) throw new InvalidDataException("预测文件没有图像。");
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var session = new DetectionReviewSession
        {
            Id = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N")[..8],
            ModelId = input.ModelId, SourcePredictionFile = full, Classes = input.Classes,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        var ids = input.Classes.Select(c => c.Id).ToHashSet();
        foreach (var item in input.Images)
        {
            if (string.IsNullOrWhiteSpace(item.ImagePath) || item.Width <= 0 || item.Height <= 0)
                throw new InvalidDataException("图像路径或尺寸无效。");
            var resolved = Path.GetFullPath(Path.IsPathRooted(item.ImagePath) ? item.ImagePath :
                Path.Combine(Path.GetDirectoryName(full)!, item.ImagePath));
            if (!File.Exists(resolved)) throw new FileNotFoundException("预测图像不存在。", resolved);
            if (!usedPaths.Add(resolved)) throw new InvalidDataException("预测清单包含重复图像：" + resolved);
            if (item.Boxes is null) throw new InvalidDataException("boxes不能为null；无目标请使用空数组。");
            foreach (var box in item.Boxes) ValidateBox(box, item.Width, item.Height, ids);
            var (priority, reason) = Rank(item.Boxes);
            session.Images.Add(new DetectionReviewImage
            {
                ImagePath = resolved, Width = item.Width, Height = item.Height, GroupId = item.GroupId,
                PredictedBoxes = item.Boxes.ToList(), Priority = priority, Reason = reason
            });
        }
        if (persist) await SaveAsync(session);
        return session;
    }

    public async Task SaveAsync(DetectionReviewSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Id) || session.Id.Contains('/') || session.Id.Contains('\\') || session.Id.Contains(':'))
            throw new InvalidDataException("无效的审核会话ID。");
        Directory.CreateDirectory(_sessions);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        var path = Path.Combine(_sessions, session.Id + ".json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(session, Json));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<DetectionReviewSession?> LoadLatestAsync()
    {
        if (!Directory.Exists(_sessions)) return null;
        foreach (var file in Directory.EnumerateFiles(_sessions, "*.json")
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            try
            {
                var session = JsonSerializer.Deserialize<DetectionReviewSession>(await File.ReadAllTextAsync(file), Json);
                if (session is { SchemaVersion: 1 } && session.Images is not null && session.Classes is not null)
                    return session;
            }
            catch (Exception error) when (error is IOException or JsonException) { /* Skip damaged sessions. */ }
        }
        return null;
    }

    public async Task<string> ExportApprovedCocoAsync(DetectionReviewSession session)
    {
        var approved = session.Images.Where(image => image.Status == "approved").ToArray();
        if (approved.Length == 0) throw new InvalidOperationException("没有人工确认的图片，禁止导出训练标签。");
        var export = Path.Combine(_exports, DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        var imageFolder = Path.Combine(export, "images");
        Directory.CreateDirectory(imageFolder);
        var images = new List<object>();
        var annotations = new List<object>();
        var annotationId = 1;
        for (var i = 0; i < approved.Length; i++)
        {
            var item = approved[i];
            ValidateReviewedBoxes(item, item.ReviewedBoxes, session.Classes);
            if (!File.Exists(item.ImagePath)) throw new FileNotFoundException("审核图像不存在。", item.ImagePath);
            var imageId = i + 1;
            var name = imageId.ToString("D6") + "_" + Path.GetFileName(item.ImagePath);
            File.Copy(item.ImagePath, Path.Combine(imageFolder, name), overwrite: false);
            images.Add(new { id = imageId, file_name = name, width = item.Width, height = item.Height });
            foreach (var box in item.ReviewedBoxes)
                annotations.Add(new
                {
                    id = annotationId++, image_id = imageId, category_id = box.ClassId,
                    bbox = new[] { box.Col1, box.Row1, box.Col2 - box.Col1, box.Row2 - box.Row1 },
                    halcon_bbox = new[] { box.Row1, box.Col1, box.Row2, box.Col2 },
                    area = (box.Col2 - box.Col1) * (box.Row2 - box.Row1), iscrowd = 0
                });
        }
        await File.WriteAllTextAsync(Path.Combine(export, "annotations.json"), JsonSerializer.Serialize(new
        {
            info = new { description = "Human-approved active-learning detection annotations", version = "1.0" },
            images, annotations, categories = session.Classes.Select(c => new { id = c.Id, name = c.Name }).ToArray()
        }, Json));
        await File.WriteAllTextAsync(Path.Combine(export, "provenance.json"), JsonSerializer.Serialize(new
        {
            sessionId = session.Id, session.ModelId, session.SourcePredictionFile, exportedAt = DateTimeOffset.UtcNow,
            approvedImages = approved.Length, approvedBoxes = annotationId - 1, unreviewedImages = session.Images.Count - approved.Length,
            note = "Only approved labels exported. Rework/pending predictions are not ground truth."
        }, Json));
        return export;
    }
}
