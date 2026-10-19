using System.Security.Cryptography;
using System.Text.Json;
using HalconDotNet;
using HalconTraining.Halcon.Datasets;
using HalconTraining.Halcon.Training;

string Option(string name) { var index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:throw new ArgumentException("缺少 "+name); }
string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
void Require(bool value,string message){if(!value)throw new InvalidDataException(message);}
try
{
    var source=Path.GetFullPath(Option("--model"));
    var dataset=Path.GetFullPath(Option("--dataset"));
    var output=Path.GetFullPath(Option("--output"));
    if(Directory.Exists(output))throw new IOException("验证目录需为新目录，避免覆盖已有结果。");
    Directory.CreateDirectory(output);
    var sourceHash=Hash(source);var datasetHash=Hash(dataset);
    var sourcePreprocess=Path.Combine(Path.GetDirectoryName(source)!,"preprocess.hdict");
    var preprocessHash=Hash(sourcePreprocess);
    var data=DetectionDatasetFile.Read(dataset);
    var checks=new List<string>();
    var overwriteRejected=false;
    try {DetectionBootstrapTrainer.Train(dataset,"unused",Path.GetDirectoryName(source)!+Path.DirectorySeparatorChar,1,4,256,256,.0001,_=>{},source);}
    catch(InvalidOperationException){overwriteRejected=true;}
    Require(overwriteRejected,"来源模型目录未被保护");checks.Add("禁止覆盖来源模型目录（含末尾分隔符）");
    var occupied=Path.Combine(output,"occupied");Directory.CreateDirectory(occupied);
    File.WriteAllText(Path.Combine(occupied,"sentinel"),"keep");
    overwriteRejected=false;
    try {DetectionBootstrapTrainer.Train(dataset,"unused",occupied,1,4,256,256,.0001,_=>{},source);}
    catch(IOException){overwriteRejected=true;}
    Require(overwriteRejected&&File.ReadAllText(Path.Combine(occupied,"sentinel"))=="keep","已有结果目录未被保护");
    checks.Add("禁止覆盖已有结果目录");
    using(var model=DetectionModelInitializer.Create("unused-for-warm-start",source,data.Classes,256,256))
    {
        DetectionModelInitializer.Validate(model,data.Classes,256,256);checks.Add("原生模型类别与尺寸匹配");
        HOperatorSet.ReadDict(Path.Combine(Path.GetDirectoryName(source)!,"preprocess.hdict"),new HTuple(),new HTuple(),out var preprocess);
        using(preprocess)
        {
            DetectionModelInitializer.ValidatePreprocess(model,preprocess);checks.Add("继承配套预处理参数");
            HOperatorSet.SetDictTuple(preprocess,"image_width",512);
            var rejected=false;
            try{DetectionModelInitializer.ValidatePreprocess(model,preprocess);}catch(InvalidDataException){rejected=true;}
            Require(rejected,"错误的配套预处理未被拒绝");checks.Add("预处理不匹配被拒绝");
        }
        void Reject((int Id,string Name)[] classes,int size,string label)
        {
            var rejected=false;
            try{DetectionModelInitializer.Validate(model,classes,size,size);}catch(InvalidDataException){rejected=true;}
            Require(rejected,label);checks.Add(label);
        }
        Reject(data.Classes,512,"尺寸不匹配被拒绝");
        Reject(data.Classes.Select(c=>(c.Id+100,c.Name)).ToArray(),256,"类别 ID 不匹配被拒绝");
        Reject(data.Classes.Select(c=>(c.Id,c.Name+"changed")).ToArray(),256,"类别名称不匹配被拒绝");
        Reject(data.Classes.Reverse().ToArray(),256,"类别顺序不匹配被拒绝");
    }
    // No GPU operation is performed without the explicit --train switch.
    if(args.Contains("--train"))
    {
        foreach(var process in System.Diagnostics.Process.GetProcessesByName("HalconTraining.Worker"))
            using(process)if(!process.HasExited)throw new InvalidOperationException("正式 Worker 正在运行；请在空闲 GPU 上执行 --train 验证。");
        var selected=data.Images.GroupBy(i=>(i.Split,i.Boxes.FirstOrDefault()?.ClassId??-1))
            .SelectMany(group=>group.Take(group.Key.Split=="train"?4:1)).ToArray();
        HOperatorSet.ReadDict(dataset,new HTuple(),new HTuple(),out var original);
        using(original)
        {
            HOperatorSet.GetDictTuple(original,"samples",out var originals);
            using(originals)
            using(var subset=new HTuple())
            {
                foreach(var item in selected)
                {
                    var index=Array.FindIndex(data.Images,i=>i.Id==item.Id);
                    subset[subset.Length]=originals[index];
                }
                HOperatorSet.CreateDict(out var reduced);
                using(reduced)
                {
                    HOperatorSet.SetDictTuple(reduced,"class_ids",new HTuple(data.Classes.Select(c=>c.Id).ToArray()));
                    HOperatorSet.SetDictTuple(reduced,"class_names",new HTuple(data.Classes.Select(c=>c.Name).ToArray()));
                    HOperatorSet.SetDictTuple(reduced,"image_dir",Path.GetDirectoryName(selected[0].Path)!);
                    HOperatorSet.SetDictTuple(reduced,"samples",subset);
                    var snapshot=Path.Combine(output,"subset.hdict");
                    HOperatorSet.WriteDict(reduced,snapshot,new HTuple(),new HTuple());
                    var events=new List<JsonElement>();
                    DetectionBootstrapTrainer.Train(snapshot,"unused-for-warm-start",Path.Combine(output,"training"),
                        2,4,256,256,.0001,item=>{var ev=JsonSerializer.SerializeToElement(item);events.Add(ev);Console.WriteLine(ev);},source);
                    using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"training","manifest.json")));
                    var root=manifest.RootElement;
                    Require(root.GetProperty("initialModelFile").GetString()==source,"实际微调来源错误");
                    Require(root.GetProperty("images").GetInt32()==selected.Count(i=>i.Split=="train"),"验证/测试图像进入优化器");
                    Require(root.GetProperty("performance").GetArrayLength()==2,"缺少逐轮性能报告");
                    Require(events.Count(ev=>ev.GetProperty("stage").GetString()=="轮次完成")==2,"轮次事件丢失");
                    using var checkpoint=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"training","checkpoint.json")));
                    Require(checkpoint.RootElement.GetProperty("completedEpochs").GetInt32()==2,"检查点轮次错误");
                    Require(File.Exists(Path.Combine(output,"training","model.hdl")),"检查点模型未保存");
                    checks.Add("真实 GPU 微调两轮、划分隔离、检查点、性能报告");
                }
            }
        }
    }
    Require(Hash(source)==sourceHash&&Hash(dataset)==datasetHash&&Hash(sourcePreprocess)==preprocessHash,"来源模型、预处理或数据被修改");
    checks.Add("来源模型与数据 SHA-256 未改变");
    File.WriteAllText(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{passed=true,gpuTrainingExecuted=args.Contains("--train"),checks},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine(JsonSerializer.Serialize(new{passed=true,checks}));return 0;
}
catch(Exception error){Console.Error.WriteLine(error);return 1;}
