using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using HalconDotNet;
using HalconTraining.Domain.Training;

namespace HalconTraining.Halcon.Training;

/// <summary>One-job process: read-only source dataset, independent model instance and archive.</summary>
public static class SemanticTrainer
{
    public static void Train(string datasetFile, string modelFile, string outputDirectory,
        int epochs, int batchSize, int width, int height, double learningRate, Action<object> progress, int seed = 42, double trainPercent = 70, double validationPercent = 15, int validationInterval = 1)
    {
        if (epochs < 1 || batchSize < 1 || width < 1 || height < 1 || validationInterval < 1 || !double.IsFinite(learningRate) || learningRate <= 0)
            throw new ArgumentException("训练参数必须大于0。");
        Directory.CreateDirectory(outputDirectory);
        void Emit(string stage, object detail) => progress(new { stage, detail, time = DateTimeOffset.Now });
        Emit("加载模型", new { modelFile });
        HOperatorSet.ReadDlModel(modelFile, out var model);
        using (model)
        {
            HOperatorSet.GetDlModelParam(model, "type", out var modelType);
            using (modelType) if (modelType.S != "segmentation") throw new InvalidOperationException("基础模型不是语义分割模型。");
            HOperatorSet.ReadDict(datasetFile, "convert_json_arrays_to", "tuple", out var dataset);
            using (dataset)
            {
                HOperatorSet.GetDictTuple(dataset, "class_ids", out var ids);
                HOperatorSet.GetDictTuple(dataset, "class_names", out var names);
                HOperatorSet.GetDictTuple(dataset, "samples", out var sourceSamples);
                HOperatorSet.GetDictTuple(dataset, "image_dir", out var imageRoot);
                HOperatorSet.GetDictTuple(dataset, "segmentation_dir", out var maskRoot);
                using (ids) using (names) using (sourceSamples) using (imageRoot) using (maskRoot)
                {
                    HOperatorSet.SetDlModelParam(model, "class_ids", ids);
                    HOperatorSet.SetDlModelParam(model, "batch_size", batchSize);
                    HOperatorSet.SetDlModelParam(model, "image_width", width);
                    HOperatorSet.SetDlModelParam(model, "image_height", height);
                    HOperatorSet.SetDlModelParam(model, "learning_rate", learningRate);
                    var records = new List<(string Image, string Mask)>();
                    var groups = new List<string?>();
                    var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(datasetFile))!;
                    for (var i = 0; i < sourceSamples.Length; i++)
                    {
                        using var source = sourceSamples.TupleSelect(i);
                        HOperatorSet.GetDictTuple(source, "image_file_name", out var imageName);
                        HOperatorSet.GetDictTuple(source, "segmentation_file_name", out var maskName);
                        using (imageName) using (maskName)
                        {
                            var imagePath = Path.GetFullPath(Path.Combine(baseDirectory, imageRoot.S, imageName.S));
                            var maskPath = Path.GetFullPath(Path.Combine(baseDirectory, maskRoot.S, maskName.S));
                            if (!File.Exists(imagePath) || !File.Exists(maskPath)) throw new FileNotFoundException($"图像或标注缺失：{imagePath} / {maskPath}");
                            records.Add((imagePath, maskPath));
                            HOperatorSet.GetDictParam(source,"keys",new HTuple(),out var keys);
                            using(keys)
                            {
                                var groupKey = keys.ToSArr().Contains("workpiece_id") ? "workpiece_id" : keys.ToSArr().Contains("batch_id") ? "batch_id" : null;
                                if(groupKey is null)groups.Add(null);
                                else { HOperatorSet.GetDictTuple(source,groupKey,out var group);using(group)groups.Add(group.Length>0?group[0].ToString():null); }
                            }
                        }
                    }
                    var split = DatasetSplit.Create(groups,seed,trainPercent,validationPercent);
                    var random = new Random(seed);
                    var trainCount = split.Train.Length;
                    var validationCount = split.Validation.Length;
                    if (trainCount < batchSize || validationCount < 1 || split.Test.Length < 1)
                        throw new InvalidOperationException("样本不足以生成训练、验证和测试集合。");
                    File.WriteAllText(Path.Combine(outputDirectory,"split.json"), JsonSerializer.Serialize(split,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    Emit("初始化GPU", new { trainCount, validationCount, testCount = split.Test.Length });
                    HOperatorSet.QueryAvailableDlDevices("runtime", "gpu", out var devices);
                    using (devices)
                    {
                        if (devices.Length == 0) throw new InvalidOperationException("HALCON未发现可用于训练的GPU。");
                        using var device = devices.TupleSelect(0);
                        HOperatorSet.SetDlModelParam(model, "device", device);
                        HOperatorSet.SetDlModelParam(model, "runtime_init", "immediately");
                    }
                    using var engine = new HDevEngine();
                    engine.SetProcedurePath(Path.Combine(Environment.GetEnvironmentVariable("HALCONROOT") ?? throw new InvalidOperationException("HALCONROOT未设置。"),"procedures","dl"));
                    using var create = new HDevProcedure("create_dl_preprocess_param_from_model");
                    using var createCall = new HDevProcedureCall(create);
                    createCall.SetInputCtrlParamTuple("DLModelHandle", model);
                    createCall.SetInputCtrlParamTuple("NormalizationType", new HTuple("none"));
                    createCall.SetInputCtrlParamTuple("DomainHandling", new HTuple("full_domain"));
                    createCall.SetInputCtrlParamTuple("SetBackgroundID", new HTuple());
                    createCall.SetInputCtrlParamTuple("ClassIDsBackground", new HTuple());
                    createCall.SetInputCtrlParamTuple("GenParam", new HTuple());
                    createCall.Execute();
                    using var preprocess = createCall.GetOutputCtrlParamTuple("DLPreprocessParam");
                    HOperatorSet.WriteDict(preprocess, Path.Combine(outputDirectory,"preprocess.hdict"), new HTuple(), new HTuple());
                    using var preprocessProcedure = new HDevProcedure("preprocess_dl_samples");
                    var recentLosses=new Queue<double>();
                    for (var epoch = 1; epoch <= epochs; epoch++)
                    {
                        var epochTimer=Stopwatch.StartNew();
                        var shuffled = split.Train.OrderBy(_ => random.Next()).ToArray();
                        var lossTotal = 0.0;
                        var batches = 0;
                        for (var offset = 0; offset < shuffled.Length; offset += batchSize)
                        {
                            var iterationTimer=Stopwatch.StartNew();
                            using var batch = new HTuple();
                            for (var j = 0; j < batchSize; j++)
                            {
                                var record = records[shuffled[(offset+j)%shuffled.Length]];
                                HOperatorSet.ReadImage(out var image, record.Image);
                                HOperatorSet.ReadImage(out var mask, record.Mask);
                                using (image) using (mask)
                                {
                                    HOperatorSet.CreateDict(out var sample);
                                    using (sample)
                                    {
                                        HOperatorSet.SetDictObject(image,sample,"image");
                                        HOperatorSet.SetDictObject(mask,sample,"segmentation_image");
                                        batch[j] = sample[0];
                                    }
                                }
                            }
                            using var preprocessCall = new HDevProcedureCall(preprocessProcedure);
                            preprocessCall.SetInputCtrlParamTuple("DLSampleBatch", batch);
                            preprocessCall.SetInputCtrlParamTuple("DLPreprocessParam", preprocess);
                            preprocessCall.Execute();
                            for (var j = 0; j < batchSize; j++)
                            {
                                using var sample = batch.TupleSelect(j);
                                HOperatorSet.GenImageConst(out var zeros,"real",width,height);
                                using (zeros)
                                {
                                    HOperatorSet.ScaleImage(zeros,out var weights,0,1);
                                    using (weights) HOperatorSet.SetDictObject(weights,sample,"weight_image");
                                }
                            }
                            HOperatorSet.TrainDlModelBatch(model,batch,out var result);
                            double batchLoss;
                            using (result)
                            {
                                HOperatorSet.GetDictTuple(result,"total_loss",out var loss);
                                using (loss){batchLoss=loss.D;lossTotal += batchLoss;}
                            }
                            batches++;
                            recentLosses.Enqueue(batchLoss);while(recentLosses.Count>Math.Max(1,1000/batchSize))recentLosses.Dequeue();
                            Emit("训练", new { epoch, epochs, batch = batches, batches = (int)Math.Ceiling((double)trainCount/batchSize), loss = batchLoss, meanLoss=recentLosses.Average(), epochLoss=lossTotal/batches, learningRate, iterationMs=iterationTimer.Elapsed.TotalMilliseconds });
                        }
                        HOperatorSet.WriteDlModel(model,Path.Combine(outputDirectory,"model.hdl"));
                        TrainingValidation.Metrics? validation=null;
                        if(epoch%validationInterval==0||epoch==epochs)
                        {
                            Emit("轮次验证",new {epoch,epochs,count=split.Validation.Length});
                            validation=TrainingValidation.Evaluate(model,preprocess,preprocessProcedure,records,split.Validation,batchSize,ids.ToIArr(),outputDirectory,epoch);
                        }
                        var metrics=new {epoch,epochs,loss=lossTotal/batches,learningRate,validationIoU=validation?.MeanIoU,validationCount=validation?.Count,epochSeconds=epochTimer.Elapsed.TotalSeconds,previewImage=validation?.PreviewImage,previewMask=validation?.PreviewMask,truthMask=validation?.TruthMask};
                        File.AppendAllText(Path.Combine(outputDirectory,"epoch_metrics.jsonl"),JsonSerializer.Serialize(metrics)+System.Environment.NewLine);
                        Emit("轮次完成",metrics);
                    }
                    var manifest = new { status="trained_pending_validation", task="semantic_segmentation", epochs, batchSize, width, height, learningRate, datasetSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(datasetFile))), classIds=ids.ToIArr(), classNames=names.ToSArr() };
                    File.WriteAllText(Path.Combine(outputDirectory,"manifest.json"),JsonSerializer.Serialize(manifest));
                    Emit("训练完成", new { outputDirectory, validation = "待加载与评估验证" });
                }
            }
        }
    }
}
