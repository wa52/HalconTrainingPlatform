using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HalconDotNet;
using HalconTraining.Halcon.Datasets;

string? Option(string key){var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:null;}
void RequireIdleWorker()
{
    foreach(var process in Process.GetProcessesByName("HalconTraining.Worker"))
        using(process)if(!process.HasExited)throw new InvalidOperationException("正式 Worker 正在运行，停止性能基准以避免抢占 GPU 或产生无效对比。");
}
try
{
    var file=Option("--dataset")??throw new ArgumentException("缺少 --dataset");
    var preprocessFile=Option("--preprocess")??throw new ArgumentException("缺少 --preprocess");
    var output=Option("--output")??throw new ArgumentException("缺少 --output");
    var batchSize=int.Parse(Option("--batch")??"8",CultureInfo.InvariantCulture);
    var repeats=int.Parse(Option("--repeats")??"3",CultureInfo.InvariantCulture);
    var cached=args.Contains("--cached");var fusion=args.Contains("--fusion");
    var all=DetectionDatasetFile.Read(file);
    var samples=all.Images.Where(i=>i.Split=="train").GroupBy(i=>i.Boxes.FirstOrDefault()?.ClassId??-1)
        .SelectMany(group=>group.Take(4)).ToArray();
    if(samples.Length%batchSize!=0||repeats<1)throw new ArgumentException("基准样本数必须为Batch的倍数。");
    using var engine=new HDevEngine();engine.SetProcedurePath(Path.Combine(Environment.GetEnvironmentVariable("HALCONROOT")!,"procedures","dl"));
    using var procedure=new HDevProcedure("preprocess_dl_samples");
    HOperatorSet.ReadDict(preprocessFile,new HTuple(),new HTuple(),out var preprocess);
    using var parameters=preprocess;
    HTuple Prepare(IReadOnlyList<DetectionDatasetImage> records)
    {
        var batch=new HTuple();
        try
        {
            for(var j=0;j<records.Count;j++)
            {
                using var image=new HImage(records[j].Path);
                HOperatorSet.CreateDict(out var sample);
                using(sample){HOperatorSet.SetDictObject(image,sample,"image");DetectionDatasetFile.SetBoxes(sample,records[j].Boxes);batch[j]=sample[0];}
            }
            using var call=new HDevProcedureCall(procedure);call.SetInputCtrlParamTuple("DLSampleBatch",batch);call.SetInputCtrlParamTuple("DLPreprocessParam",preprocess);call.Execute();
            return batch;
        }
        catch{batch.Dispose();throw;}
    }
    using var prepared=Prepare(samples);
    HTuple CachedBatch(int offset)
    {
        var batch=new HTuple();
        try
        {
            for(var j=0;j<batchSize;j++)
            {
                using var source=prepared.TupleSelect(offset+j);
                HOperatorSet.CopyDict(source,new HTuple(),new HTuple(),out var copy);
                using(copy)batch[j]=copy[0];
            }
            return batch;
        }
        catch{batch.Dispose();throw;}
    }
    var preparation=new List<double>();var training=new List<double>();var losses=new List<double>();
    int layersBefore=0,layersAfter=0;double initializationMs=0,warmupMs=0;
    using var modelHolder=new HTuple();
    if(Option("--model") is { } modelFile)
    {
        RequireIdleWorker();
        var initialization=Stopwatch.StartNew();HOperatorSet.ReadDlModel(modelFile,out var model);
        using(model)modelHolder[0]=model[0];
        HOperatorSet.GetDlModelParam(modelHolder,"layer_names",out var before);using(before)layersBefore=before.Length;
        if(fusion){HOperatorSet.SetDlModelParam(modelHolder,"fuse_bn_relu","true");HOperatorSet.SetDlModelParam(modelHolder,"fuse_conv_relu","true");}
        HOperatorSet.GetDlModelParam(modelHolder,"layer_names",out var after);using(after)layersAfter=after.Length;
        HOperatorSet.SetDlModelParam(modelHolder,"batch_size",batchSize);HOperatorSet.SetDlModelParam(modelHolder,"learning_rate",.0001);
        HOperatorSet.QueryAvailableDlDevices("runtime","gpu",out var devices);
        using(devices){using var device=devices.TupleSelect(0);HOperatorSet.SetDlModelParam(modelHolder,"device",device);}
        initializationMs=initialization.Elapsed.TotalMilliseconds;
        using var warmup=CachedBatch(0);initialization.Restart();HOperatorSet.TrainDlModelBatch(modelHolder,warmup,out var warmupResult);warmupResult.Dispose();warmupMs=initialization.Elapsed.TotalMilliseconds;
    }
    // Actual preprocessing and optimizer calls; warmup is excluded and all cases use the same image order.
    for(var repeat=0;repeat<repeats;repeat++)
    for(var offset=0;offset<samples.Length;offset+=batchSize)
    {
        var clock=Stopwatch.StartNew();using var batch=cached?CachedBatch(offset):Prepare(samples.Skip(offset).Take(batchSize).ToArray());
        preparation.Add(clock.Elapsed.TotalMilliseconds);
        if(modelHolder.Length>0)
        {
            RequireIdleWorker();
            clock.Restart();HOperatorSet.TrainDlModelBatch(modelHolder,batch,out var result);
            training.Add(clock.Elapsed.TotalMilliseconds);
            using(result){HOperatorSet.GetDictTuple(result,"total_loss",out var loss);using(loss)losses.Add(loss.D);}
        }
    }
    if(losses.Any(loss=>!double.IsFinite(loss)))throw new InvalidDataException("GPU基准Loss异常。");
    var report=new {batchSize,repeats,cached,fusion,layersBefore,layersAfter,initializationMs,warmupMs,images=samples.Length*repeats,
        preparationMs=preparation.Sum(),trainingMs=training.Sum(),
        imagesPerSecond=1000.0*samples.Length*repeats/(preparation.Sum()+training.Sum()),losses,
        note="隔离基准：仅训练集样本，不保存或登记模型，不改变原始数据、标注、现有模型。"};
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine(JsonSerializer.Serialize(report));return 0;
}
catch(Exception error){Console.Error.WriteLine(error);return 1;}
