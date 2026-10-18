using System.Security.Cryptography;
using System.Text.Json;
using HalconDotNet;
using HalconTraining.Halcon.Datasets;

namespace HalconTraining.Halcon.Training;

/// <summary>
/// Small-sample detection bootstrap. The result is deliberately UNVALIDATED:
/// it can generate review proposals, never a production accuracy claim.
/// </summary>
public static class DetectionBootstrapTrainer
{

    public static object Train(string cocoFile, string backboneFile, string outputDirectory,
        int epochs, int batchSize, int width, int height, double learningRate, Action<object> progress)
    {
        if (!File.Exists(cocoFile) || !File.Exists(backboneFile))
            throw new FileNotFoundException("找不到检测数据集或 HALCON 预训练 backbone。");
        if (epochs < 1 || batchSize < 1 || width < 64 || height < 64 ||
            width % 32 != 0 || height % 32 != 0 || learningRate <= 0 || !double.IsFinite(learningRate))
            throw new ArgumentException("参数无效：尺寸需要≥64且为32倍数，Batch/Epoch/学习率必须大于零。");

        var nativeDataset = Path.GetExtension(cocoFile).Equals(".hdict", StringComparison.OrdinalIgnoreCase);
        var data = nativeDataset ? DetectionDatasetFile.Read(cocoFile) : DetectionDatasetFile.ReadCoco(cocoFile);
        var classPairs = data.Classes;
        var labelIds = classPairs.Select(c => c.Id).ToArray();
        // Validation/test samples never enter the optimizer.
        var samples = data.Images.Where(image => image.Split == "train").ToArray();
        if (data.Images.Length < 5 || samples.Length == 0 || samples.Length < batchSize)
            throw new InvalidDataException("数据集总计至少需要5张已确认图片，且训练集不得为空、Batch不能大于训练集数量。");
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
        // Detection input dimensions are creation parameters in HALCON 24.11, not settable afterwards.
        HOperatorSet.CreateDict(out var parameters);
        using var detectionParameters = parameters;
        HOperatorSet.SetDictTuple(parameters,"image_width",width);
        HOperatorSet.SetDictTuple(parameters,"image_height",height);
        HOperatorSet.CreateDlModelDetection(backboneFile, classPairs.Length, parameters, out var model);
        using (model)
        {
            HOperatorSet.SetDlModelParam(model, "class_ids", new HTuple(labelIds));
            HOperatorSet.SetDlModelParam(model, "class_names", new HTuple(classPairs.Select(c => c.Name).ToArray()));
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
                            image.GetImageSize(out int actualWidth, out int actualHeight);
                            if (actualWidth != item.Width || actualHeight != item.Height)
                                throw new InvalidDataException("实际图像尺寸与数据集标注不一致：" + item.Path);
                            HOperatorSet.CreateDict(out var sample);
                            using (sample)
                            {
                                HOperatorSet.SetDictObject(image, sample, "image");
                                DetectionDatasetFile.SetBoxes(sample, item.Boxes);
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
                images = samples.Length, totalImages = data.Images.Length, validationImages = data.Images.Count(i => i.Split == "validation"),
                testImages = data.Images.Count(i => i.Split == "test"), sourceDataset = Path.GetFullPath(cocoFile),
                labelBoxes = samples.Sum(s => s.Boxes.Length), lossHistory,
                warning = "小样本初始训练模型，仅供未标注图片预标注；未执行独立mAP/召回率验收，禁止用于生产判定。"
            };
            File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"),
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            return new { outputDirectory, modelFile = Path.Combine(outputDirectory, "model.hdl"),
                status = "bootstrap_unvalidated", images = samples.Length, epochs };
        }
    }
}
