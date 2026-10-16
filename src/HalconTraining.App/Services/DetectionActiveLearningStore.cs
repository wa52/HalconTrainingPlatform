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
    public List<DetectionBox> PredictedBoxes { get; set; } = [];
    public List<DetectionBox> ReviewedBoxes { get; set; } = [];
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
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<DetectionCategory> Classes { get; set; } = [];
    public List<DetectionReviewImage> Images { get; set; } = [];
}

/// <summary>
/// Validates predictions, ranks an active-learning review queue, saves reviews atomically,
/// and exports only human-approved boxes to COCO. Does not modify original images/datasets.
/// </summary>
public sealed class DetectionActiveLearningStore(string workspaceRoot)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _sessions = Path.Combine(workspaceRoot, "active-learning", "sessions");
    private readonly string _exports = Path.Combine(workspaceRoot, "active-learning", "exports");

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

    public static IReadOnlyList<DetectionReviewImage> Pending(DetectionReviewSession session) =>
        session.Images.Where(image => image.Status == "pending")
            .OrderByDescending(image => image.Priority).ThenBy(image => image.ImagePath, StringComparer.OrdinalIgnoreCase).ToArray();

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

    public async Task<DetectionReviewSession> ImportAsync(string filename)
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
        await SaveAsync(session);
        return session;
    }

    public async Task SaveAsync(DetectionReviewSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Id) || session.Id.IndexOfAny(['/', '\\', ':']) >= 0)
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
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
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
