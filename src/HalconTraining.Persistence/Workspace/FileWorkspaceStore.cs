using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HalconTraining.Application.Workspace;

namespace HalconTraining.Persistence.Workspace;

public sealed class FileWorkspaceStore(string root) : IWorkspaceStore
{
    public string Root { get; } = Path.GetFullPath(root);
    private static readonly string[] Terminal = ["completed", "failed", "interrupted"];
    private static async Task WriteAsync(string file, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, WorkspaceJson.Options));
        File.Move(temporary, file, true);
    }
    public string NewRun(string dataset, string task = "semantic_segmentation")
    {
        if (task is not ("semantic_segmentation" or "classification")) throw new ArgumentException("无效任务类型。");
        var directory = Path.Combine(Root, "models", task, Path.GetFileNameWithoutExtension(dataset), "run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);return directory;
    }
    public string NewTest(string modelDirectory)
    {
        var directory = Path.Combine(Root, "results", "tests", new DirectoryInfo(modelDirectory).Parent!.Name, Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);return directory;
    }
    public async Task SaveStateAsync(string directory, string status, string? error = null)
    {
        var file = Path.Combine(directory, "job.json");
        var state = File.Exists(file) ? JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject() : new JsonObject { ["startedAt"] = DateTimeOffset.Now };
        state["status"] = status; state["error"] = error; state["updatedAt"] = DateTimeOffset.Now;
        if (Terminal.Contains(status)) state["endedAt"] = DateTimeOffset.Now;
        await WriteAsync(file, state);
    }
    public async Task PrepareAsync(string directory, TrainingSettings settings)
    {
        await WriteAsync(Path.Combine(directory, "training_params.json"), settings);
        await SaveStateAsync(directory, "preparing");
    }
    public async Task CompleteAsync(string directory)
    {
        var manifestFile = Path.Combine(directory, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestFile))!.AsObject();
        manifest["status"] = "reload_and_evaluation_passed";
        manifest["completedAt"] = DateTimeOffset.Now;
        manifest["evaluationReport"] = "validation_report.json";
        await WriteAsync(Path.Combine(directory, "classes.json"), new JsonObject { ["classIds"] = manifest["classIds"]?.DeepClone(), ["classNames"] = manifest["classNames"]?.DeepClone() });
        await WriteAsync(manifestFile, manifest);
        var hashes = new Dictionary<string,string>();
        foreach (var name in new[] { "model.hdl", "preprocess.hdict", "split.json", "classes.json", "manifest.json", "training_params.json", "validation_report.json", "training.log", "epoch_metrics.jsonl" })
            hashes[name] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, name))));
        await WriteAsync(Path.Combine(directory, "checksums.json"), hashes);
    }
    public async Task<IReadOnlyList<ModelEntry>> ModelsAsync()
    {
        var library = Path.Combine(Root, "models");
        if (!Directory.Exists(library)) return [];
        var directories = Directory.EnumerateFiles(library, "manifest.json", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(library,"job.json",SearchOption.AllDirectories)).Select(file => Path.GetDirectoryName(file)!).Distinct().ToArray();
        var models = new List<ModelEntry>();
        foreach (var directory in directories)
        {
            try
            {
                var manifestFile=Path.Combine(directory,"manifest.json");
                var manifest = File.Exists(manifestFile) ? JsonNode.Parse(await File.ReadAllTextAsync(manifestFile))! : new JsonObject();
                var stateFile = Path.Combine(directory, "job.json");
                var status = manifest["status"]?.GetValue<string>() ?? "unknown";
                if (File.Exists(stateFile))
                {
                    var state=JsonNode.Parse(await File.ReadAllTextAsync(stateFile))!;
                    var jobStatus=state["status"]?.GetValue<string>();
                    if(jobStatus is "failed" or "interrupted")status=jobStatus;
                }
                var metrics = "尚无独立评估";
                var evaluation=Path.Combine(directory,"validation_report.json");
                if(File.Exists(evaluation))
                {
                    var report=JsonNode.Parse(await File.ReadAllTextAsync(evaluation))!;
                    if(manifest["task"]?.GetValue<string>()=="classification")
                        metrics=$"验证准确率 {report["reports"]?["validation"]?["accuracy"]?.GetValue<double>():P2} · 测试准确率 {report["reports"]?["test"]?["accuracy"]?.GetValue<double>():P2}";
                    else metrics=$"验证 mIoU {report["reports"]?["validation"]?["meanIoU"]?.GetValue<double>():F4} · 测试 mIoU {report["reports"]?["test"]?["meanIoU"]?.GetValue<double>():F4}";
                }
                models.Add(new(directory,new DirectoryInfo(directory).Parent!.Name,manifest["task"]?.GetValue<string>()??new DirectoryInfo(directory).Parent!.Parent!.Name,status,metrics));
            }
            catch(Exception error) when(error is IOException or JsonException or InvalidOperationException) { models.Add(new(directory,new DirectoryInfo(directory).Name,"unknown","invalid",error.Message)); }
        }
        return models.OrderByDescending(model=>Path.GetFileName(model.Directory),StringComparer.Ordinal).ToArray();
    }
    public async Task ValidateModelAsync(string directory)
    {
        var hashes=JsonSerializer.Deserialize<Dictionary<string,string>>(await File.ReadAllTextAsync(Path.Combine(directory,"checksums.json")))??throw new InvalidDataException("模型校验文件为空。");
        foreach(var name in new[]{"model.hdl","preprocess.hdict","split.json","classes.json","validation_report.json"})
            if(!hashes.ContainsKey(name))throw new InvalidDataException("模型包未包含必要校验项："+name);
        foreach(var pair in hashes)
        {
            if(Path.GetFileName(pair.Key)!=pair.Key)throw new InvalidDataException("模型包校验路径无效。");
            var actual=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory,pair.Key))));
            if(!string.Equals(actual,pair.Value,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("模型包文件已改变："+pair.Key);
        }
    }
    public async Task<IReadOnlyList<PredictionResult>> ResultsAsync(string modelDirectory)
    {
        var root=Path.Combine(Root,"results","tests",new DirectoryInfo(modelDirectory).Parent!.Name);
        if(!Directory.Exists(root))return [];
        var results=new List<PredictionResult>();
        foreach(var file in Directory.EnumerateFiles(root,"result.json",SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(200))
        {
            try
            {
                var json=await File.ReadAllTextAsync(file);using var doc=JsonDocument.Parse(json);
                if(!string.Equals(doc.RootElement.GetProperty("modelFile").GetString(),Path.Combine(modelDirectory,"model.hdl"),StringComparison.OrdinalIgnoreCase))continue;
                var result=JsonSerializer.Deserialize<PredictionResult>(json,WorkspaceJson.Options);
                if(result is not null&&File.Exists(result.OriginalPath)&&(result.MaskPath is null||File.Exists(result.MaskPath)))results.Add(result);
            }
            catch(Exception error) when(error is IOException or JsonException or KeyNotFoundException) { /* A damaged result must not hide healthy history. */ }
        }
        return results;
    }
    public Task SaveSessionAsync(DesktopSession session)=>WriteAsync(Path.Combine(Root,"artifacts","desktop-session.json"),session);
    public async Task<DesktopSession?> LoadSessionAsync()
    {
        var library=Path.Combine(Root,"models");
        if(Directory.Exists(library))
            foreach(var file in Directory.EnumerateFiles(library,"job.json",SearchOption.AllDirectories))
            {
                try
                {
                    var state=JsonNode.Parse(await File.ReadAllTextAsync(file));
                    if(state?["status"]?.GetValue<string>() is "preparing" or "training" or "validating")
                        await SaveStateAsync(Path.GetDirectoryName(file)!,"interrupted","上次程序未正常结束；产物保留，请重新启动训练。");
                }
                catch(Exception error) when(error is IOException or JsonException or InvalidOperationException) { /* Corrupt job metadata is isolated by ModelsAsync. */ }
            }
        var session=Path.Combine(Root,"artifacts","desktop-session.json");
        if(!File.Exists(session))return null;
        try{return JsonSerializer.Deserialize<DesktopSession>(await File.ReadAllTextAsync(session),WorkspaceJson.Options);}
        catch(JsonException){return null;}
    }
}
