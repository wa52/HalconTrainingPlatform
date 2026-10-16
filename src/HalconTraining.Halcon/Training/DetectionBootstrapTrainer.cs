using System.Security.Cryptography;
using System.Text.Json;
using HalconDotNet;

namespace HalconTraining.Halcon.Training;

/// <summary>
/// Small-sample detection bootstrap. The result is deliberately UNVALIDATED:
/// it can generate review proposals, never a production accuracy claim.
/// </summary>
public static class DetectionBootstrapTrainer
{
    private sealed record Box(int ClassId, double Col1, double Row1, double Col2, double Row2);
    private sealed record Sample(string Path, Box[] Boxes);

    public static object Train(string cocoFile, string backboneFile, string outputDirectory,
        int epochs, int batchSize, int width, int height, double learningRate, Action<object> progress)
    {
        if (!File.Exists(cocoFile) || !File.Exists(backboneFile))
            throw new FileNotFoundException("找不到人工标注 COCO 或 HALCON 预训练 backbone。");
        if (epochs < 1 || batchSize < 1 || width < 64 || height < 64 ||
            width % 32 != 0 || height % 32 != 0 || learningRate <= 0 || !double.IsFinite(learningRate))
            throw new ArgumentException("参数无效：尺寸需要≥64且为32倍数，Batch/Epoch/学习率必须大于零。");

        using var doc = JsonDocument.Parse(File.ReadAllText(cocoFile));
        var root = doc.RootElement;
        var classPairs = root.GetProperty("categories").EnumerateArray()
            .Select(value => (Id: value.GetProperty("id").GetInt32(), Name: value.GetProperty("name").GetString() ?? ""))
            .ToArray();
        if (classPairs.Length == 0 || classPairs.Any(c => string.IsNullOrWhiteSpace(c.Name)) ||
            classPairs.Select(c => c.Id).Distinct().Count() != classPairs.Length)
            throw new InvalidDataException("COCO 类别列表无效。");
        var labelIds = classPairs.Select(c => c.Id).ToArray();
        var boxesByImage = root.GetProperty("annotations").EnumerateArray()
            .GroupBy(value => value.GetProperty("image_id").GetInt32())
            .ToDictionary(group => group.Key, group => group.Select(value =>
            {
                var rect = value.GetProperty("bbox").EnumerateArray().Select(x => x.GetDouble()).ToArray();
                if (rect.Length != 4) throw new InvalidDataException("COCO bbox 必须为 x,y,w,h。");
                var categoryId = value.GetProperty("category_id").GetInt32();
                if (!labelIds.Contains(categoryId)) throw new InvalidDataException("检测标注引用了未定义类别。");
                return new Box(categoryId, rect[0], rect[1], rect[0] + rect[2], rect[1] + rect[3]);
            }).ToArray());
        var imagesDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cocoFile))!, "images");
        var samples = root.GetProperty("images").EnumerateArray().Select(image =>
        {
            var id = image.GetProperty("id").GetInt32();
            var filename = image.GetProperty("file_name").GetString() ?? "";
            // Exported image names have no path separators; prevent reading outside the snapshot.
            if (filename != Path.GetFileName(filename) || filename.Length == 0)
                throw new InvalidDataException("COCO 图像路径必须位于 images 目录。");
            var imageWidth = image.GetProperty("width").GetInt32();
            var imageHeight = image.GetProperty("height").GetInt32();
            var boxes = boxesByImage.GetValueOrDefault(id, []);
            if (boxes.Any(box => box.Col1 < 0 || box.Row1 < 0 || box.Col2 > imageWidth ||
                box.Row2 > imageHeight || box.Col1 >= box.Col2 || box.Row1 >= box.Row2))
                throw new InvalidDataException("COCO 检测框越界。");
            return new Sample(Path.Combine(imagesDirectory, filename), boxes);
        }).ToArray();

        if (samples.Length < 3 || samples.Length < batchSize)
            throw new InvalidDataException("初始训练至少需要3张已人工确认的图片，且Batch不能大于图片数。");
        if (samples.SelectMany(s => s.Boxes).Any() == false)
            throw new InvalidDataException("没有目标框；已审核无目标图片可参与训练，但必须同时有正样本。");
        var missing = classPairs.Where(c => !samples.SelectMany(s => s.Boxes).Any(b => b.ClassId == c.Id))
            .Select(c => c.Name).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("以下类别没有已审核的检测框：" + string.Join("、", missing));
        foreach (var item in samples)
            if (!File.Exists(item.Path)) throw new FileNotFoundException("COCO 图像缺失。", item.Path);
        Directory.CreateDirectory(outputDirectory);
        void Emit(string stage, object detail)
        {
            var ev = new { stage, detail, time = DateTimeOffset.Now };
            var line = JsonSerializer.Serialize(ev);
            File.AppendAllText(Path.Combine(outputDirectory, "training.log"), line + Environment.NewLine);
            progress(ev);
        }

        // HALCON 24.11 generates a detector from the shipped pretrained classifier backbone.
        HOperatorSet.CreateDlModelDetection(backboneFile, classPairs.Length, new HTuple(), out var model);
        using (model)
        {
            HOperatorSet.SetDlModelParam(model, "class_ids", new HTuple(labelIds));
            HOperatorSet.SetDlModelParam(model, "class_names", new HTuple(classPairs.Select(c => c.Name).ToArray()));
            HOperatorSet.SetDlModelParam(model, "image_width", width);
            HOperatorSet.SetDlModelParam(model, "image_height", height);
            HOperatorSet.SetDlModelParam(model, "batch_size", batchSize);
            HOperatorSet.SetDlModelParam(model, "learning_rate", learningRate);
            HOperatorSet.QueryAvailableDlDevices("runtime", "gpu", out var devices);
            using (devices)
            {
                if (devices.Length == 0) throw new InvalidOperationException("当前 HALCON 没有可训练检测模型的 GPU。");
                using var device = devices.TupleSelect(0);
                HOperatorSet.SetDlModelParam(model, "device", device);
                HOperatorSet.SetDlModelParam(model, "runtime_init", "immediately");
            }

            using var engine = new HDevEngine();
            engine.SetProcedurePath(Path.Combine(
                Environment.GetEnvironmentVariable("HALCONROOT") ?? throw new InvalidOperationException("未设置HALCONROOT。"),
                "procedures", "dl"));
            using var create = new HDevProcedure("create_dl_preprocess_param_from_model");
            using var creator = new HDevProcedureCall(create);
            creator.SetInputCtrlParamTuple("DLModelHandle", model);
            creator.SetInputCtrlParamTuple("NormalizationType", new HTuple("none"));
            creator.SetInputCtrlParamTuple("DomainHandling", new HTuple("full_domain"));
            creator.SetInputCtrlParamTuple("SetBackgroundID", new HTuple());
            creator.SetInputCtrlParamTuple("ClassIDsBackground", new HTuple());
            creator.SetInputCtrlParamTuple("GenParam", new HTuple());
            creator.Execute();
            using var preprocess = creator.GetOutputCtrlParamTuple("DLPreprocessParam");
            HOperatorSet.WriteDict(preprocess, Path.Combine(outputDirectory, "preprocess.hdict"), new HTuple(), new HTuple());
            using var preprocessing = new HDevProcedure("preprocess_dl_samples");
            var rng = new Random(42);
            var lossHistory = new List<double>();
            Emit("初始检测训练", new { images = samples.Length, categories = classPairs.Length, epochs, batchSize });
            for (var epoch = 1; epoch <= epochs; epoch++)
            {
                var indices = Enumerable.Range(0, samples.Length).OrderBy(_ => rng.Next()).ToArray();
                double sumLoss = 0;
                var batches = 0;
                for (var offset = 0; offset < indices.Length; offset += batchSize)
                {
                    var batch = new HTuple();
                    try
                    {
                        for (var j = 0; j < batchSize; j++)
                        {
                            var item = samples[indices[(offset + j) % indices.Length]];
                            using var image = new HImage(item.Path);
                            HOperatorSet.CreateDict(out var sample);
                            using (sample)
                            {
                                HOperatorSet.SetDictObject(image, sample, "image");
                                HOperatorSet.SetDictTuple(sample, "bbox_label_id", new HTuple(item.Boxes.Select(b => b.ClassId).ToArray()));
                                HOperatorSet.SetDictTuple(sample, "bbox_row1", new HTuple(item.Boxes.Select(b => b.Row1).ToArray()));
                                HOperatorSet.SetDictTuple(sample, "bbox_col1", new HTuple(item.Boxes.Select(b => b.Col1).ToArray()));
                                HOperatorSet.SetDictTuple(sample, "bbox_row2", new HTuple(item.Boxes.Select(b => b.Row2).ToArray()));
                                HOperatorSet.SetDictTuple(sample, "bbox_col2", new HTuple(item.Boxes.Select(b => b.Col2).ToArray()));
                                batch[j] = sample[0];
                            }
                        }
                        using (var call = new HDevProcedureCall(preprocessing))
                        {
                            call.SetInputCtrlParamTuple("DLSampleBatch", batch);
                            call.SetInputCtrlParamTuple("DLPreprocessParam", preprocess);
                            call.Execute();
                        }
                        HOperatorSet.TrainDlModelBatch(model, batch, out var result);
                        using (result)
                        {
                            HOperatorSet.GetDictTuple(result, "total_loss", out var loss);
                            using (loss)
                            {
                                if (!double.IsFinite(loss.D)) throw new InvalidDataException("检测训练Loss无效。");
                                sumLoss += loss.D;
                            }
                        }
                        batches++;
                    }
                    finally { batch.Dispose(); }
                    Emit("训练", new { epoch, epochs, batch = batches,
                        batches = (int)Math.Ceiling((double)samples.Length / batchSize),
                        meanLoss = sumLoss / batches });
                }
                lossHistory.Add(sumLoss / batches);
                HOperatorSet.WriteDlModel(model, Path.Combine(outputDirectory, "model.hdl"));
                Emit("轮次完成", new { epoch, epochs, loss = sumLoss / batches });
            }
            var info = new
            {
                status = "bootstrap_unvalidated", task = "detection", instanceType = "rectangle1",
                createdAt = DateTimeOffset.UtcNow, epochs, batchSize, width, height, learningRate,
                sourceAnnotationsSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cocoFile))),
                backboneFile, classIds = labelIds, classNames = classPairs.Select(c => c.Name).ToArray(),
                images = samples.Length, labelBoxes = samples.Sum(s => s.Boxes.Length), lossHistory,
                warning = "小样本初始训练模型，仅供未标注图片预标注；未执行独立mAP/召回率验收，禁止用于生产判定。"
            };
            File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"),
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            return new { outputDirectory, modelFile = Path.Combine(outputDirectory, "model.hdl"),
                status = "bootstrap_unvalidated", images = samples.Length, epochs };
        }
    }
}
