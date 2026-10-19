using System.IO;
using System.Text.Json;

namespace HalconTraining.App.Services;

public sealed record HalconDetectionModelSource(string? InitialModelFile,string Description)
{
    public static HalconDetectionModelSource Resolve(DetectionReviewSession scope,int imageSize,bool usePrevious,string? selectedFile=null)
    {
        if(!string.IsNullOrWhiteSpace(selectedFile))
        {
            if(!File.Exists(selectedFile)||!Path.GetExtension(selectedFile).Equals(".hdl",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("手选的续训模型不存在或不是 HALCON .hdl 文件。");
            return new(Path.GetFullPath(selectedFile),$"继续微调 {Path.GetFileName(Path.GetDirectoryName(selectedFile))}；追加 Epoch，需同目录 preprocess.hdict。");
        }
        if(!usePrevious)return new(null,"从 HALCON 基础模型重新训练。");
        var model=scope.LastTrainedModelPath;
        if(model is null||!File.Exists(model)||!Path.GetExtension(model).Equals(".hdl",StringComparison.OrdinalIgnoreCase))
            return new(null,"没有可复用的同任务 HALCON 模型，从基础模型训练。");
        try
        {
            using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(model)!,"manifest.json")));
            var data=manifest.RootElement;
            if(data.GetProperty("task").GetString()!="detection"||data.GetProperty("width").GetInt32()!=imageSize||
                data.GetProperty("height").GetInt32()!=imageSize||
                !data.GetProperty("classIds").EnumerateArray().Select(v=>v.GetInt32()).SequenceEqual(scope.Classes.Select(c=>c.Id))||
                !data.GetProperty("classNames").EnumerateArray().Select(v=>v.GetString()).SequenceEqual(scope.Classes.Select(c=>c.Name)))
                return new(null,"上次模型的类别或尺寸不同，从基础模型训练。");
            return new(Path.GetFullPath(model),"继续微调上次完成的 HALCON 模型；本轮 Epoch 为追加轮数。");
        }
        catch(Exception error) when(error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {return new(null,"上次模型记录无法核对，从 HALCON 基础模型训练。");}
    }
}
