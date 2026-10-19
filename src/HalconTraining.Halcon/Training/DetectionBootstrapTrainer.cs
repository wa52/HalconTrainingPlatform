using System.Security.Cryptography;
using System.Diagnostics;
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
        int epochs, int batchSize, int width, int height, double learningRate, Action<object> progress, string? initialModelFile = null)
    {
        if (!File.Exists(cocoFile) || !File.Exists(initialModelFile ?? backboneFile))
            throw new FileNotFoundException("找不到检测数据集或 HALCON 预训练 backbone。");
        if (epochs < 1 || batchSize < 1 || width < 64 || height < 64 ||
            width % 32 != 0 || height % 32 != 0 || learningRate <= 0 || !double.IsFinite(learningRate))
            throw new ArgumentException("参数无效：尺寸需要≥64且为32倍数，Batch/Epoch/学习率必须大于零。");

        if (initialModelFile is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory)),
            Path.GetDirectoryName(Path.GetFullPath(initialModelFile)),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("续训输出必须使用新目录，禁止覆盖来源模型。");
        if(Directory.Exists(outputDirectory)&&Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException("训练输出目录必须为空，禁止覆盖已有结果。");
        var totalClock=Stopwatch.StartNew();
        var nativeDataset = Path.GetExtension(cocoFile).Equals(".hdict", StringComparison.OrdinalIgnoreCase);
        var data = nativeDataset ? DetectionDatasetFile.Read(cocoFile) : DetectionDatasetFile.ReadCoco(cocoFile);
        var classPairs = data.Classes;
        var labelIds = classPairs.Select(c => c.Id).ToArray();
        // Validation/test samples never enter the optimizer.
        var samples = data.Images.Where(image => image.Split == "train").ToArray();
        if (data.Images.Length < 5 || samples.Length == 0 || samples.Length < batchSize)
            throw new InvalidDataException("数据集总计至少需要5张图片，且训练集不得为空、Batch不能大于训练集数量。");
        if (samples.SelectMany(s => s.Boxes).Any() == false)
            throw new InvalidDataException("没有目标框；无框负样本可参与训练，但必须同时有正样本。");
        var missing = classPairs.Where(c => !samples.SelectMany(s => s.Boxes).Any(b => b.ClassId == c.Id))
            .Select(c => c.Name).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("以下类别没有检测框：" + string.Join("、", missing));
        foreach (var item in samples)
            if (!File.Exists(item.Path)) throw new FileNotFoundException("COCO 图像缺失。", item.Path);
        Directory.CreateDirectory(outputDirectory);
        using var log=new StreamWriter(Path.Combine(outputDirectory,"training.log"),append:false) {AutoFlush=true};
        string Hash(string path) {using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
        void WriteReport(string name,object value)
        {
            var path=Path.Combine(outputDirectory,name);
            var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                File.WriteAllText(temporary,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
                File.Move(temporary,path,overwrite:true);
            }
            finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        var sourceHash=Hash(cocoFile);
        var modelSource=Path.GetFullPath(initialModelFile ?? backboneFile);
        var modelSourceHash=Hash(modelSource);
        var sourcePreprocess=initialModelFile is null ? null : Path.Combine(Path.GetDirectoryName(modelSource)!,"preprocess.hdict");
        var sourcePreprocessHash=sourcePreprocess is not null && File.Exists(sourcePreprocess) ? Hash(sourcePreprocess) : null;
        WriteReport("training_params.json",new {
            epochs,batchSize,width,height,learningRate,initialModelFile,modelSource,modelSourceHash,
            sourcePreprocess,sourcePreprocessHash,
            sourceDataset=Path.GetFullPath(cocoFile),sourceHash,seed=42,
            initialization=initialModelFile is null?"pretrained_backbone":"warm_start_weights",
            note="本轮追加Epoch；微调复用权重，不声明恢复上一任务的优化器、随机数和训练轮次状态。"
        });
        void Emit(string stage, object detail)
        {
            var ev = new { stage, detail, time = DateTimeOffset.Now };
            var line = JsonSerializer.Serialize(ev);
            log.WriteLine(line);
            progress(ev);
        }

        Emit(initialModelFile is null ? "初始化基础模型" : "继续微调已有模型",new {modelSource,epochs});
        using var model=DetectionModelInitializer.Create(backboneFile,initialModelFile,classPairs,width,height);
        using var engine = new HDevEngine();
        engine.SetProcedurePath(Path.Combine(
            Environment.GetEnvironmentVariable("HALCONROOT") ?? throw new InvalidOperationException("未设置HALCONROOT。"),
            "procedures", "dl"));
        HTuple CreatePreprocess()
        {
            if(initialModelFile is not null)
            {
                var file=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(initialModelFile))!,"preprocess.hdict");
                if(!File.Exists(file))throw new FileNotFoundException("续训模型需要同目录的 preprocess.hdict。",file);
                HOperatorSet.ReadDict(file,new HTuple(),new HTuple(),out var existing);
                try {DetectionModelInitializer.ValidatePreprocess(model,existing);return existing;}
                catch {existing.Dispose();throw;}
            }
            using var create = new HDevProcedure("create_dl_preprocess_param_from_model");
            using var creator = new HDevProcedureCall(create);
            creator.SetInputCtrlParamTuple("DLModelHandle", model);
            creator.SetInputCtrlParamTuple("NormalizationType", new HTuple("none"));
            creator.SetInputCtrlParamTuple("DomainHandling", new HTuple("full_domain"));
            creator.SetInputCtrlParamTuple("SetBackgroundID", new HTuple());
            creator.SetInputCtrlParamTuple("ClassIDsBackground", new HTuple());
            creator.SetInputCtrlParamTuple("GenParam", new HTuple());
            creator.Execute();
            return creator.GetOutputCtrlParamTuple("DLPreprocessParam");
        }
        using var preprocess=CreatePreprocess();
        {
            Emit("初始化 GPU",new {modelSource,epochs,batchSize});
            HOperatorSet.SetDlModelParam(model,"batch_size_multiplier",1);
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

            HOperatorSet.WriteDict(preprocess, Path.Combine(outputDirectory, "preprocess.hdict"), new HTuple(), new HTuple());
            using var preprocessing = new HDevProcedure("preprocess_dl_samples");
            var rng = new Random(42);
            var lossHistory = new List<double>();
            var performance=new List<object>();
            Emit("初始检测训练", new { images = samples.Length, categories = classPairs.Length, epochs, batchSize });
            for (var epoch = 1; epoch <= epochs; epoch++)
            {
                var epochClock=Stopwatch.StartNew();
                double readTotal=0,preprocessTotal=0,trainTotal=0;
                var indices = Enumerable.Range(0, samples.Length).OrderBy(_ => rng.Next()).ToArray();
                double sumLoss = 0;
                var batches = 0;
                for (var offset = 0; offset < indices.Length; offset += batchSize)
                {
                    if(epoch==1&&offset==0)Emit("首批训练（含 GPU 预热，请稍候）",new {images=samples.Length,batchSize});
                    var batch = new HTuple();
                    var stepClock=Stopwatch.StartNew();
                    double readMs=0,preprocessMs=0,trainingMs=0;
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
                        readMs=stepClock.Elapsed.TotalMilliseconds;stepClock.Restart();
                        using (var call = new HDevProcedureCall(preprocessing))
                        {
                            call.SetInputCtrlParamTuple("DLSampleBatch", batch);
                            call.SetInputCtrlParamTuple("DLPreprocessParam", preprocess);
                            call.Execute();
                        }
                        preprocessMs=stepClock.Elapsed.TotalMilliseconds;stepClock.Restart();
                        HOperatorSet.TrainDlModelBatch(model, batch, out var result);
                        trainingMs=stepClock.Elapsed.TotalMilliseconds;
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
                    readTotal+=readMs;preprocessTotal+=preprocessMs;trainTotal+=trainingMs;
                    Emit("训练", new { epoch, epochs, batch = batches,readMs,preprocessMs,trainingMs,
                        imagesPerSecond=batchSize*1000.0/Math.Max(1,readMs+preprocessMs+trainingMs),
                        batches = (int)Math.Ceiling((double)samples.Length / batchSize),
                        meanLoss = sumLoss / batches });
                }
                lossHistory.Add(sumLoss / batches);
                var saveClock=Stopwatch.StartNew();
                var temporary=Path.Combine(outputDirectory,"checkpoint_"+Guid.NewGuid().ToString("N")+".tmp.hdl");
                try
                {
                    HOperatorSet.WriteDlModel(model,temporary);
                    File.Move(temporary,Path.Combine(outputDirectory,"model.hdl"),overwrite:true);
                }
                finally{if(File.Exists(temporary))File.Delete(temporary);}
                var checkpointMs=saveClock.Elapsed.TotalMilliseconds;
                var metrics=new {epoch,epochs,loss=sumLoss/batches,readMs=readTotal,preprocessMs=preprocessTotal,
                    trainingMs=trainTotal,checkpointMs,seconds=epochClock.Elapsed.TotalSeconds,
                    imagesPerSecond=samples.Length/Math.Max(.001,epochClock.Elapsed.TotalSeconds)};
                performance.Add(metrics);
                WriteReport("performance.json",performance);
                WriteReport("checkpoint.json",new {
                    status="checkpoint_only",completedEpochs=epoch,plannedEpochs=epochs,sourceDataset=Path.GetFullPath(cocoFile),
                    sourceHash,classIds=labelIds,classNames=classPairs.Select(c=>c.Name).ToArray(),width,height,
                    batchSize,learningRate,initialModelFile,
                    note="已完成轮次的权重检查点，未发布为完成模型；可手选以继续微调。"
                });
                Emit("轮次完成",metrics);
            }
            var info = new
            {
                status = "bootstrap_unvalidated", task = "detection", instanceType = "rectangle1",
                createdAt = DateTimeOffset.UtcNow, epochs, batchSize, width, height, learningRate,
                sourceAnnotationsSha256 = sourceHash,
                initialModelFile,modelSource,modelSourceHash,sourcePreprocess,sourcePreprocessHash,totalSeconds=totalClock.Elapsed.TotalSeconds,performance,
                backboneFile, classIds = labelIds, classNames = classPairs.Select(c => c.Name).ToArray(),
                images = samples.Length, totalImages = data.Images.Length, validationImages = data.Images.Count(i => i.Split == "validation"),
                testImages = data.Images.Count(i => i.Split == "test"), sourceDataset = Path.GetFullPath(cocoFile),
                labelBoxes = samples.Sum(s => s.Boxes.Length), lossHistory,
                warning = "检测模型未执行独立mAP/召回率验收；可用于人工预标注，禁止直接用于生产判定。"
            };
            WriteReport("manifest.json",info);
            return new { outputDirectory, modelFile = Path.Combine(outputDirectory, "model.hdl"),
                status = "bootstrap_unvalidated", images = samples.Length, epochs };
        }
    }
}
