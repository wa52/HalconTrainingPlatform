using System.IO;
using System.Text.Json;

namespace HalconTraining.App.Services;

public sealed partial class DetectionActiveLearningStore
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    /// <summary>Direct child folders define classes; source files are read in place, never labelled implicitly.</summary>
    public async Task<DetectionReviewSession> ImportFolderAsync(string directory,
        Func<string, (int Width, int Height)> readSize, DetectionReviewSession? previous = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("图片根目录不存在：" + root);
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = false
        };
        var folders = Directory.EnumerateDirectories(root, "*", options)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
        if (folders.Length == 0)
            throw new InvalidDataException("请选择包含类别子文件夹的根目录；每个子文件夹对应一个类别。");
        var sameRoot = previous?.SourceDirectory is { } oldRoot &&
            string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldRoot)), StringComparison.OrdinalIgnoreCase);
        var session = sameRoot
            ? JsonSerializer.Deserialize<DetectionReviewSession>(JsonSerializer.Serialize(previous, Json), Json)!
            : new DetectionReviewSession
            {
                Id = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N")[..8],
                ModelId = "manual-seed", CreatedAt = DateTimeOffset.UtcNow
            };
        session.SourceDirectory = root;
        session.DatasetName = Path.GetFileName(root);
        session.ImportIssues.Clear();
        var existing = session.Images.ToDictionary(image => Path.GetFullPath(image.ImagePath), StringComparer.OrdinalIgnoreCase);
        var removed = session.RemovedImagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextId = session.Classes.Select(category => category.Id).DefaultIfEmpty(0).Max() + 1;
        var recursive = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = false,
            AttributesToSkip = options.AttributesToSkip
        };
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(folder);
            var category = session.Classes.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (category is null) { category = new DetectionCategory(nextId++, name); session.Classes.Add(category); }
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", recursive).Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                session.ImportIssues.Add($"无法读取类别 {name}：{error.Message}");
                continue;
            }
            var readable = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(file);
                if (removed.Contains(full)) continue;
                try
                {
                    var (width, height) = readSize(full);
                    if (width <= 0 || height <= 0) throw new InvalidDataException("图像尺寸无效");
                    if (existing.TryGetValue(full, out var old))
                    {
                        if (old.Width != width || old.Height != height)
                            throw new InvalidDataException("原图尺寸已改变，请检查原有标注");
                        old.FolderClassId = category.Id;
                    }
                    else
                    {
                        var image = new DetectionReviewImage
                        {
                            ImagePath = full, Width = width, Height = height, FolderClassId = category.Id,
                            Priority = 1, Reason = "待手工框选 · 目录类别：" + category.Name
                        };
                        session.Images.Add(image);
                        existing.Add(full, image);
                    }
                    readable++;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException)
                { session.ImportIssues.Add($"跳过图片 {full}：{error.Message}"); }
            }
            if (readable == 0) session.ImportIssues.Add("类别没有可读取的图片：" + name);
            progress?.Report($"正在读取 {name} · 已发现 {session.Images.Count:N0} 张图片");
        }
        if (session.Images.Count == 0 && !(sameRoot && removed.Count > 0)) throw new InvalidDataException("类别子文件夹中没有可读取的图像。");
        if (Directory.EnumerateFiles(root, "*", options).Any(path => ImageExtensions.Contains(Path.GetExtension(path))))
            session.ImportIssues.Add("根目录中的图片没有类别，已跳过；请放入对应的类别子文件夹。");
        cancellationToken.ThrowIfCancellationRequested();
        await SaveAsync(session);
        return session;
    }

    public static IReadOnlyList<DetectionReviewImage> PredictionCandidates(DetectionReviewSession session) =>
        session.Images.Where(image => image.Status == "pending" && !image.HasDraft).ToArray();

    /// <summary>Final multi-class dataset: only explicitly approved images and their saved reviewed boxes.</summary>
    public static DetectionReviewSession CombinedApprovedScope(DetectionReviewSession session)
    {
        var images = session.Images.Where(i=>i.Status=="approved").ToList();
        var used = images.SelectMany(i=>i.ReviewedBoxes.Select(b=>b.ClassId))
            .Concat(images.Where(i=>i.FolderClassId.HasValue).Select(i=>i.FolderClassId!.Value)).ToHashSet();
        var classes = session.Classes.Where(c=>used.Contains(c.Id)).ToList();
        if (images.Count==0 || classes.Count==0) throw new InvalidOperationException("没有已确认的检测标注可合并。");
        return new DetectionReviewSession
        {
            Id=session.Id+"_combined",DatasetName=string.IsNullOrWhiteSpace(session.SourceDirectory)?session.DatasetName:
                new DirectoryInfo(session.SourceDirectory).Name,
            SourceDirectory=session.SourceDirectory,ModelId=session.ModelId,SourcePredictionFile=session.SourcePredictionFile,
            CreatedAt=session.CreatedAt,UpdatedAt=session.UpdatedAt,Classes=classes,Images=images,
            LastTrainedModelPath=session.CombinedModelPath
        };
    }

    /// <summary>One folder, one class, one independent training/prediction scope.</summary>
    public static DetectionReviewSession FolderScope(DetectionReviewSession session, int classId)
    {
        var category = session.Classes.Single(category => category.Id == classId);
        return new DetectionReviewSession
        {
            Id = session.Id + "_class_" + classId, DatasetName = category.Name,
            SourceDirectory = session.SourceDirectory is null ? null : Path.Combine(session.SourceDirectory, category.Name),
            ModelId = session.ModelId, CreatedAt = session.CreatedAt, UpdatedAt = session.UpdatedAt,
            LastTrainedModelPath = session.FolderModels.GetValueOrDefault(classId),
            Classes = [category], Images = session.Images.Where(image => image.FolderClassId == classId).ToList(),
            RemovedImagePaths = session.RemovedImagePaths.ToList()
        };
    }

    public static DetectionReviewSession MergeFolderPredictions(DetectionReviewSession session, int classId, DetectionReviewSession predictions)
    {
        var scope = FolderScope(session, classId);
        var known = scope.Images.Select(image => image.ImagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = session.RemovedImagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (predictions.Images.Any(image => !known.Contains(image.ImagePath) && !removed.Contains(Path.GetFullPath(image.ImagePath))))
            throw new InvalidDataException("预测包含当前类别文件夹以外的图片，未覆盖标注。");
        var predictedScope = MergePredictions(scope, predictions);
        var merged = JsonSerializer.Deserialize<DetectionReviewSession>(JsonSerializer.Serialize(session, Json), Json)!;
        var updates = predictedScope.Images.ToDictionary(image => image.ImagePath, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < merged.Images.Count; i++)
            if (merged.Images[i].FolderClassId == classId && updates.TryGetValue(merged.Images[i].ImagePath, out var image))
                merged.Images[i] = image;
        merged.SourcePredictionFile = predictions.SourcePredictionFile;
        return merged;
    }

    /// <summary>Merge only compatible proposals; keep every existing image, draft and human decision.</summary>
    public static DetectionReviewSession MergePredictions(DetectionReviewSession previous, DetectionReviewSession predictions)
    {
        if (previous.Classes.Count != predictions.Classes.Count ||
            previous.Classes.Any(c => !predictions.Classes.Any(other => other.Id == c.Id && other.Name == c.Name)))
            throw new InvalidDataException("模型类别与当前文件夹类别不一致，未覆盖已有标注。请使用本轮训练模型。");
        var merged = JsonSerializer.Deserialize<DetectionReviewSession>(JsonSerializer.Serialize(previous, Json), Json)!;
        var existing = merged.Images.ToDictionary(image => image.ImagePath, StringComparer.OrdinalIgnoreCase);
        var removed = merged.RemovedImagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var proposal in predictions.Images)
        {
            if (removed.Contains(Path.GetFullPath(proposal.ImagePath))) continue;
            if (!existing.TryGetValue(proposal.ImagePath, out var image))
            { merged.Images.Add(proposal); continue; }
            if (image.Status != "pending" || image.HasDraft) continue;
            if (image.Width != proposal.Width || image.Height != proposal.Height)
                throw new InvalidDataException("预测图像尺寸与当前数据集不一致：" + image.ImagePath);
            image.PredictedBoxes = proposal.PredictedBoxes.ToList();
            image.Priority = proposal.Priority;
            image.Reason = proposal.Reason;
        }
        merged.ModelId = predictions.ModelId;
        merged.SourcePredictionFile = predictions.SourcePredictionFile;
        return merged;
    }
}
