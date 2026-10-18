using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using HalconTraining.Application.Workers;
using HalconTraining.Halcon.Devices;
using HalconTraining.Persistence.Environment;
using HalconTraining.Halcon.Datasets;
using HalconTraining.Halcon.Training;

var detectionExport = GetOption(args, "--detection-export-hdict");
if (detectionExport is not null)
{
    try
    {
        Console.WriteLine(JsonSerializer.Serialize(DetectionDatasetFile.Export(detectionExport,
            GetOption(args,"--output") ?? throw new ArgumentException("缺少--output"))));
        return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}

var detectionSeed = GetOption(args, "--detection-train") ?? GetOption(args, "--detection-bootstrap");
if (detectionSeed is not null)
{
    try
    {
        var outcome = DetectionBootstrapTrainer.Train(detectionSeed,
            GetOption(args, "--backbone-file") ?? throw new ArgumentException("缺少--backbone-file"),
            GetOption(args, "--output-dir") ?? throw new ArgumentException("缺少--output-dir"),
            int.Parse(GetOption(args, "--epochs") ?? "5"),
            int.Parse(GetOption(args, "--batch") ?? "1"),
            int.Parse(GetOption(args, "--width") ?? "512"),
            int.Parse(GetOption(args, "--height") ?? "512"),
            double.Parse(GetOption(args, "--rate") ?? "0.0001", System.Globalization.CultureInfo.InvariantCulture),
            item => { Console.WriteLine(JsonSerializer.Serialize(item)); Console.Out.Flush(); });
        Console.WriteLine(JsonSerializer.Serialize(outcome));
        return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}

var detectionModel = GetOption(args, "--detection-predict");
if (detectionModel is not null)
{
    try
    {
        var outcome = DetectionPredictor.Predict(detectionModel,
            GetOption(args, "--image-list") ?? throw new ArgumentException("缺少--image-list"),
            GetOption(args, "--output-json") ?? throw new ArgumentException("缺少--output-json"),
            item => { Console.WriteLine(JsonSerializer.Serialize(item)); Console.Out.Flush(); },
            double.Parse(GetOption(args, "--confidence") ?? "0.05", System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine(JsonSerializer.Serialize(outcome));
        return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}

var overlayImage=GetOption(args,"--render-overlay");
if(overlayImage is not null)
{
    try
    {
        SemanticTester.SaveOverlay(overlayImage,GetOption(args,"--mask")??throw new ArgumentException("缺少掩膜。"),GetOption(args,"--output")??throw new ArgumentException("缺少输出路径。"),double.Parse(GetOption(args,"--opacity")??"0.6",System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine("{\"status\":\"saved\"}");return 0;
    }
    catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
}
var classifier=args.Any(a=>a is "--classification-train" or "--classification-evaluate" or "--classification-test");
var testImage = GetOption(args,classifier?"--classification-test":"--semantic-test");
if(testImage is not null)
{
    try
    {
        Console.WriteLine(JsonSerializer.Serialize((classifier?(Func<string,string,string,object>)ClassificationPipeline.Test:SemanticTester.Test)(testImage,
            GetOption(args,"--model-dir") ?? throw new ArgumentException("缺少--model-dir。"),
            GetOption(args,"--output") ?? throw new ArgumentException("缺少--output。")),new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 0;
    }
    catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
}

var evaluationDataset = GetOption(args, classifier?"--classification-evaluate":"--semantic-evaluate");
if(evaluationDataset is not null)
{
    try
    {
        Console.WriteLine(JsonSerializer.Serialize((classifier?(Func<string,string,object>)ClassificationPipeline.Evaluate:SemanticEvaluator.Evaluate)(evaluationDataset,
            GetOption(args,"--output") ?? throw new ArgumentException("缺少--output。"))));
        return 0;
    }
    catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
}

var trainingDataset = GetOption(args, classifier?"--classification-train":"--semantic-train");
if (trainingDataset is not null)
{
    try
    {
        var train=classifier?(Action<string,string,string,int,int,int,int,double,Action<object>,int,double,double,int>)ClassificationPipeline.Train:SemanticTrainer.Train;
        train(trainingDataset,
            GetOption(args,"--model") ?? throw new ArgumentException("缺少--model。"),
            GetOption(args,"--output") ?? throw new ArgumentException("缺少--output。"),
            int.Parse(GetOption(args,"--epochs") ?? "60"), int.Parse(GetOption(args,"--batch") ?? "2"),
            int.Parse(GetOption(args,"--width") ?? "512"), int.Parse(GetOption(args,"--height") ?? "512"),
            double.Parse(GetOption(args,"--rate") ?? "0.001",System.Globalization.CultureInfo.InvariantCulture),
            item => { Console.WriteLine(JsonSerializer.Serialize(item)); Console.Out.Flush(); },
            int.Parse(GetOption(args,"--seed") ?? "42"),
            double.Parse(GetOption(args,"--train-percent") ?? "70",System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(GetOption(args,"--validation-percent") ?? "15",System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(GetOption(args,"--validation-interval") ?? "1"));
        return 0;
    }
    catch(Exception error)
    {
        Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
        return 1;
    }
}

var datasetPath = GetOption(args, "--dataset-inspect");
if (datasetPath is not null)
{
    try
    {
        Console.WriteLine(JsonSerializer.Serialize(DatasetInspector.Inspect(datasetPath,
            int.Parse(GetOption(args,"--seed") ?? "42"),
            double.Parse(GetOption(args,"--train-percent") ?? "70",System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(GetOption(args,"--validation-percent") ?? "15",System.Globalization.CultureInfo.InvariantCulture)), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

var pipeName = GetOption(args, "--pipe");
var jobId = GetOption(args, "--job-id") ?? Guid.NewGuid().ToString("N");
var outputPath = GetOption(args, "--output");
if (!args.Contains("--environment-probe", StringComparer.Ordinal))
{
    Console.Error.WriteLine("用法：HalconTraining.Worker --environment-probe [--output <报告.json> | --pipe <管道名> --job-id <作业编号>]");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter() }
};
var reportJsonOptions = new JsonSerializerOptions(jsonOptions) { WriteIndented = true };

try
{
    var report = await new HalconTrainingEnvironmentProbe().CaptureAsync(cancellation.Token);
    if (!string.IsNullOrWhiteSpace(pipeName))
    {
        var message = WorkerMessage.EnvironmentProbeCompleted(jobId, report);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(30), cancellation.Token);
        await using var writer = new StreamWriter(pipe);
        await writer.WriteLineAsync(JsonSerializer.Serialize(message, jsonOptions));
        await writer.FlushAsync(cancellation.Token);
    }
    else if (!string.IsNullOrWhiteSpace(outputPath))
    {
        await new JsonTrainingEnvironmentReportStore().SaveAsync(report, outputPath, cancellation.Token);
        Console.WriteLine($"环境报告已保存：{Path.GetFullPath(outputPath)}");
    }
    else
    {
        Console.WriteLine(JsonSerializer.Serialize(report, reportJsonOptions));
    }

        return report.Checks.Any(check => check.Status == HalconTraining.Domain.Training.CapabilityStatus.Failed) ? 1 : 0;
}
catch (Exception exception)
{
    if (!string.IsNullOrWhiteSpace(pipeName))
    {
        try
        {
            var failure = new WorkerMessage(1, jobId, 1, DateTimeOffset.UtcNow, "WorkerFailed",
                JsonSerializer.SerializeToElement(new WorkerFailure(exception.GetType().Name, exception.Message)));
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await using var writer = new StreamWriter(pipe);
            await writer.WriteLineAsync(JsonSerializer.Serialize(failure, jsonOptions));
            await writer.FlushAsync();
        }
        catch
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
        }
    }
    else
    {
        Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    }

    return 1;
}

static string? GetOption(string[] arguments, string option)
{
    var index = Array.IndexOf(arguments, option);
    if (index < 0) return null;
    if (index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]))
        throw new ArgumentException($"参数 {option} 后必须提供值。");
    return arguments[index + 1];
}
