using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HalconDotNet;
using HalconTraining.Domain.Training;

namespace HalconTraining.Halcon.Training;

/// <summary>Single-label classifier. All native resources stay in the one-job Worker.</summary>
public static class ClassificationPipeline
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record Sample(string Image, int Label, string? Group);
    private sealed record Data(int[] SourceIds, string[] Names, Sample[] Samples);
    public sealed record Score(int ClassId, string ClassName, double Confidence);
    private sealed record Metrics(int Count, double Accuracy, double Top1Error, int[][] ConfusionMatrix,
        object[] PerClass, string? PreviewImage, string? TruthLabel, string? PredictedLabel, double? Confidence);
    private static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static Data ReadData(string file)
    {
        HOperatorSet.ReadDict(file, "convert_json_arrays_to", "tuple", out var dataset);
        using (dataset)
        {
            HOperatorSet.GetDictTuple(dataset, "class_ids", out var ids);
            HOperatorSet.GetDictTuple(dataset, "class_names", out var names);
            HOperatorSet.GetDictTuple(dataset, "samples", out var samples);
            HOperatorSet.GetDictTuple(dataset, "image_dir", out var directory);
            using (ids) using (names) using (samples) using (directory)
            {
                var sourceIds = ids.ToIArr(); var classNames = names.ToSArr();
                if (sourceIds.Length < 2 || sourceIds.Length != classNames.Length || sourceIds.Distinct().Count() != sourceIds.Length || classNames.Distinct().Count() != classNames.Length)
                    throw new InvalidDataException("分类数据集需要至少两个类别，且类别ID/名称必须唯一并对应。");
                var records = new List<Sample>();
                for (var i = 0; i < samples.Length; i++)
                {
                    using var sample = samples.TupleSelect(i);
                    HOperatorSet.GetDictTuple(sample, "image_file_name", out var name);
                    HOperatorSet.GetDictTuple(sample, "image_label_id", out var label);
                    HOperatorSet.GetDictParam(sample, "keys", new HTuple(), out var keys);
                    using (name) using (label) using (keys)
                    {
                        if (label.Length != 1) throw new InvalidDataException("当前入口仅支持单标签图像分类。");
                        var index = Array.IndexOf(sourceIds, label.I);
                        if (index < 0) throw new InvalidDataException("样本包含未声明的类别：" + name.S);
                        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, directory.S, name.S));
                        if (!File.Exists(path)) throw new FileNotFoundException("原图不存在：" + path);
                        string? group = null;
                        var key = keys.ToSArr().Contains("workpiece_id") ? "workpiece_id" : keys.ToSArr().Contains("batch_id") ? "batch_id" : null;
                        if (key is not null) { HOperatorSet.GetDictTuple(sample, key, out var value); using (value) if (value.Length > 0) group = value[0].ToString(); }
                        records.Add(new(path, index, group));
                    }
                }
                return new(sourceIds, classNames, records.ToArray());
            }
        }
    }

    private static HDevEngine Engine()
    {
        var engine = new HDevEngine();
        engine.SetProcedurePath(Path.Combine(Environment.GetEnvironmentVariable("HALCONROOT") ?? throw new InvalidOperationException("HALCONROOT未设置。"), "procedures", "dl"));
        return engine;
    }
    private static void Device(HTuple model, int batch)
    {
        HOperatorSet.GetDlModelParam(model, "type", out var type);
        using (type) if (type.S != "classification") throw new InvalidDataException("模型不是单标签分类模型。");
        HOperatorSet.QueryAvailableDlDevices("runtime", "gpu", out var devices);
        using (devices)
        {
            if (devices.Length == 0) throw new InvalidOperationException("HALCON未发现可用GPU。");
            using var device = devices.TupleSelect(0);
            HOperatorSet.SetDlModelParam(model, "batch_size", batch);
            HOperatorSet.SetDlModelParam(model, "device", device);
            HOperatorSet.SetDlModelParam(model, "runtime_init", "immediately");
        }
    }
    private static HTuple Batch(HDevProcedure procedure, HTuple preprocess, IReadOnlyList<Sample> records, bool labels)
    {
        var batch = new HTuple();
        try
        {
            for (var i = 0; i < records.Count; i++)
            {
                using var image = new HImage(records[i].Image);
                HOperatorSet.CreateDict(out var sample);
                using (sample)
                {
                    HOperatorSet.SetDictObject(image, sample, "image");
                    if (labels) HOperatorSet.SetDictTuple(sample, "image_label_id", records[i].Label);
                    batch[i] = sample[0];
                }
            }
            using var call = new HDevProcedureCall(procedure);
            call.SetInputCtrlParamTuple("DLSampleBatch", batch);
            call.SetInputCtrlParamTuple("DLPreprocessParam", preprocess);
            call.Execute();
            return batch;
        }
        catch { batch.Dispose(); throw; }
    }
    private static Score[] Scores(HTuple result)
    {
        HOperatorSet.GetDictTuple(result, "classification_class_ids", out var ids);
        HOperatorSet.GetDictTuple(result, "classification_class_names", out var names);
        HOperatorSet.GetDictTuple(result, "classification_confidences", out var confidence);
        using (ids) using (names) using (confidence)
            return Enumerable.Range(0, ids.Length).Select(i => new Score(ids[i].I, names[i].S, confidence[i].D)).ToArray();
    }
    private static Metrics Measure(HTuple model, HTuple preprocess, HDevProcedure procedure, Data data, int[] indices, int batchSize, string? previewDirectory = null)
    {
        if (indices.Length == 0) throw new InvalidDataException("评估集合为空。");
        var matrix = data.Names.Select(_ => new int[data.Names.Length]).ToArray();
        string? imagePath = null, truth = null, predicted = null; double? confidence = null;
        for (var offset = 0; offset < indices.Length; offset += batchSize)
        {
            var count = Math.Min(batchSize, indices.Length - offset);
            var records = Enumerable.Range(0, batchSize).Select(j => data.Samples[indices[offset + Math.Min(j, count - 1)]]).ToArray();
            using var batch = Batch(procedure, preprocess, records, false);
            HOperatorSet.ApplyDlModel(model, batch, new HTuple(), out var predictions);
            using (predictions)
                for (var j = 0; j < count; j++)
                {
                    using var result = predictions.TupleSelect(j); var top = Scores(result)[0];
                    if (top.ClassId < 0 || top.ClassId >= matrix.Length) throw new InvalidDataException("分类模型输出类别越界。");
                    matrix[records[j].Label][top.ClassId]++;
                    if (offset == 0 && j == 0 && previewDirectory is not null)
                    {
                        Directory.CreateDirectory(previewDirectory); imagePath = Path.Combine(previewDirectory, "original.png");
                        using var image = new HImage(records[j].Image); image.WriteImage("png", 0, imagePath);
                        truth = data.Names[records[j].Label]; predicted = top.ClassName; confidence = top.Confidence;
                    }
                }
        }
        var correct = Enumerable.Range(0, matrix.Length).Sum(i => matrix[i][i]);
        var perClass = Enumerable.Range(0, matrix.Length).Select(i => (object)new { classId = i, className = data.Names[i], count = matrix[i].Sum(), recall = matrix[i].Sum() == 0 ? (double?)null : (double)matrix[i][i] / matrix[i].Sum() }).ToArray();
        var metrics = new Metrics(indices.Length, (double)correct / indices.Length, 1 - (double)correct / indices.Length, matrix, perClass, imagePath, truth, predicted, confidence);
        if (previewDirectory is not null) Save(Path.Combine(previewDirectory, "result.json"), metrics);
        return metrics;
    }

    public static void Train(string datasetFile, string modelFile, string outputDirectory, int epochs, int batchSize, int width, int height,
        double learningRate, Action<object> progress, int seed = 42, double trainPercent = 70, double validationPercent = 15, int validationInterval = 1)
    {
        if (epochs < 1 || batchSize < 1 || width < 1 || height < 1 || validationInterval < 1 || !double.IsFinite(learningRate) || learningRate <= 0) throw new ArgumentException("训练参数必须为有效正数。");
        Directory.CreateDirectory(outputDirectory);
        void Emit(string stage, object detail) => progress(new { stage, detail, time = DateTimeOffset.Now });
        Emit("加载模型", new { modelFile });
        var sourceHash = Hash(datasetFile); var data = ReadData(datasetFile);
        var split = DatasetSplit.Create(data.Samples.Select(s => s.Group).ToArray(), seed, trainPercent, validationPercent);
        if (split.Train.Length < batchSize || split.Validation.Length == 0 || split.Test.Length == 0) throw new InvalidDataException("样本不足以划分训练、验证和测试集合。");
        var missing = Enumerable.Range(0, data.Names.Length).Where(id => !split.Train.Any(i => data.Samples[i].Label == id)).Select(id => data.Names[id]).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("训练集合缺少类别，请调整划分比例或种子：" + string.Join("、", missing));
        Save(Path.Combine(outputDirectory, "split.json"), split);
        HOperatorSet.ReadDlModel(modelFile, out var model);
        using (model)
        using (var engine = Engine())
        using (var procedure = new HDevProcedure("preprocess_dl_samples"))
        {
            using var names = new HTuple(data.Names);
            HOperatorSet.SetDlModelParam(model, "class_names", names);
            HOperatorSet.SetDlModelParam(model, "image_width", width);
            HOperatorSet.SetDlModelParam(model, "image_height", height);
            HOperatorSet.SetDlModelParam(model, "learning_rate", learningRate);
            Emit("初始化GPU", new { trainCount = split.Train.Length, validationCount = split.Validation.Length, testCount = split.Test.Length });
            Device(model, batchSize);
            using var create = new HDevProcedure("create_dl_preprocess_param_from_model"); using var createCall = new HDevProcedureCall(create);
            createCall.SetInputCtrlParamTuple("DLModelHandle", model);
            createCall.SetInputCtrlParamTuple("NormalizationType", new HTuple("none"));
            createCall.SetInputCtrlParamTuple("DomainHandling", new HTuple("full_domain"));
            createCall.SetInputCtrlParamTuple("SetBackgroundID", new HTuple()); createCall.SetInputCtrlParamTuple("ClassIDsBackground", new HTuple()); createCall.SetInputCtrlParamTuple("GenParam", new HTuple());
            createCall.Execute(); using var preprocess = createCall.GetOutputCtrlParamTuple("DLPreprocessParam");
            HOperatorSet.WriteDict(preprocess, Path.Combine(outputDirectory, "preprocess.hdict"), new HTuple(), new HTuple());
            var random = new Random(seed); var recent = new Queue<double>();
            for (var epoch = 1; epoch <= epochs; epoch++)
            {
                var timer = Stopwatch.StartNew(); var shuffled = split.Train.OrderBy(_ => random.Next()).ToArray(); double sum = 0; int batches = 0;
                for (var offset = 0; offset < shuffled.Length; offset += batchSize)
                {
                    var iteration = Stopwatch.StartNew();
                    var records = Enumerable.Range(0, batchSize).Select(j => data.Samples[shuffled[(offset + j) % shuffled.Length]]).ToArray();
                    using var batch = Batch(procedure, preprocess, records, true);
                    HOperatorSet.TrainDlModelBatch(model, batch, out var result);
                    double loss;
                    using (result) { HOperatorSet.GetDictTuple(result, "total_loss", out var value); using (value) loss = value.D; }
                    if (!double.IsFinite(loss)) throw new InvalidDataException("训练Loss无效，请调整学习率。");
                    sum += loss; batches++; recent.Enqueue(loss); while (recent.Count > Math.Max(1, 1000 / batchSize)) recent.Dequeue();
                    Emit("训练", new { epoch, epochs, batch = batches, batches = (int)Math.Ceiling((double)shuffled.Length / batchSize), loss, meanLoss = recent.Average(), learningRate, iterationMs = iteration.Elapsed.TotalMilliseconds });
                }
                HOperatorSet.WriteDlModel(model, Path.Combine(outputDirectory, "model.hdl"));
                Metrics? validation = null;
                if (epoch % validationInterval == 0 || epoch == epochs)
                {
                    Emit("轮次验证", new { epoch, epochs, count = split.Validation.Length });
                    validation = Measure(model, preprocess, procedure, data, split.Validation, batchSize, Path.Combine(outputDirectory, "training-preview", $"epoch_{epoch:0000}"));
                }
                var metrics = new { epoch, epochs, loss = sum / batches, learningRate, validationAccuracy = validation?.Accuracy, epochSeconds = timer.Elapsed.TotalSeconds, previewImage = validation?.PreviewImage, truthLabel = validation?.TruthLabel, predictedLabel = validation?.PredictedLabel, confidence = validation?.Confidence };
                File.AppendAllText(Path.Combine(outputDirectory, "epoch_metrics.jsonl"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + Environment.NewLine);
                Emit("轮次完成", metrics);
            }
            Save(Path.Combine(outputDirectory, "manifest.json"), new { status = "trained_pending_validation", task = "classification", epochs, batchSize, width, height, learningRate, datasetSha256 = sourceHash, classIds = Enumerable.Range(0, data.Names.Length).ToArray(), classNames = data.Names, sourceClassIds = data.SourceIds });
            Emit("训练完成", new { outputDirectory });
        }
    }

    public static object Evaluate(string datasetFile, string outputDirectory)
    {
        var data = ReadData(datasetFile);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDirectory, "manifest.json")));
        var root = manifest.RootElement;
        if (root.GetProperty("datasetSha256").GetString() != Hash(datasetFile) || !root.GetProperty("classNames").EnumerateArray().Select(x => x.GetString()).SequenceEqual(data.Names) || !root.GetProperty("sourceClassIds").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(data.SourceIds)) throw new InvalidDataException("评估数据集与训练源不一致。");
        var split = JsonSerializer.Deserialize<DatasetSplit>(File.ReadAllText(Path.Combine(outputDirectory, "split.json")), Json)!;
        HOperatorSet.ReadDlModel(Path.Combine(outputDirectory, "model.hdl"), out var model);
        using (model)
        {
            Device(model, 1);
            HOperatorSet.ReadDict(Path.Combine(outputDirectory, "preprocess.hdict"), new HTuple(), new HTuple(), out var preprocess);
            using (preprocess) using (var engine = Engine()) using (var procedure = new HDevProcedure("preprocess_dl_samples"))
            {
                var report = new { status = "reload_and_evaluation_passed", task = "classification", evaluatedAt = DateTimeOffset.Now,
                    reports = new { validation = Measure(model, preprocess, procedure, data, split.Validation, 1), test = Measure(model, preprocess, procedure, data, split.Test, 1) } };
                Save(Path.Combine(outputDirectory, "validation_report.json"), report); return report;
            }
        }
    }

    public static object Test(string imageFile, string modelDirectory, string outputDirectory)
    {
        var total = Stopwatch.StartNew(); Directory.CreateDirectory(outputDirectory); var modelFile = Path.Combine(modelDirectory, "model.hdl");
        HOperatorSet.ReadDlModel(modelFile, out var model);
        using (model)
        {
            Device(model, 1);
            HOperatorSet.ReadDict(Path.Combine(modelDirectory, "preprocess.hdict"), new HTuple(), new HTuple(), out var preprocess);
            using (preprocess) using (var engine = Engine()) using (var procedure = new HDevProcedure("preprocess_dl_samples"))
            using (var image = new HImage(imageFile))
            using (var batch = Batch(procedure, preprocess, [new(imageFile, 0, null)], false))
            {
                var inference = Stopwatch.StartNew(); HOperatorSet.ApplyDlModel(model, batch, new HTuple(), out var result); inference.Stop();
                using (result)
                {
                    var scores = Scores(result); image.GetImageSize(out int width, out int height); image.WriteImage("png", 0, Path.Combine(outputDirectory, "original.png"));
                    var report = new { file = Path.GetFileName(imageFile), width, height, inferenceMs = inference.Elapsed.TotalMilliseconds, totalMs = total.Elapsed.TotalMilliseconds,
                        modelFile, modelSha256 = Hash(modelFile), task = "classification", areas = Array.Empty<object>(), scores, className = scores[0].ClassName, confidence = scores[0].Confidence, outputDirectory };
                    Save(Path.Combine(outputDirectory, "result.json"), report); return report;
                }
            }
        }
    }
}
