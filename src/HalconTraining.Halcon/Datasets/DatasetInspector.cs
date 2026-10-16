using HalconDotNet;
using HalconTraining.Domain.Training;

namespace HalconTraining.Halcon.Datasets;

/// <summary>Read-only dictionary inspection; HALCON handles stay in the worker process.</summary>
public static class DatasetInspector
{
    public static object Inspect(string file, int seed = 42, double trainPercent = 70, double validationPercent = 15)
    {
        file = Path.GetFullPath(file);
        HOperatorSet.ReadDict(file, "convert_json_arrays_to", "tuple", out var dataset);
        using (dataset)
        {
            // Dictionary handles are reference-counted; the one-shot worker releases all resources on exit.
            {
                using var samples = Value(dataset, "samples");
                using var classes = Value(dataset, "class_names");
                using var imageDir = Value(dataset, "image_dir");
                var tasks = new HashSet<string>();
                var rows = new List<object>();
                var issues = new List<string>();
                var count = 0;
                var maskCount = 0;
                var splitCounts = new Dictionary<string, int> { ["训练集"] = 0, ["验证集"] = 0, ["测试集"] = 0 };
                var hasGroupMetadata = false;
                var groups = new List<string?>();
                for(var i=0;i<samples.Length;i++)
                {
                    using var sample=samples.TupleSelect(i);
                    var keys=Keys(sample);
                    using var group=Value(sample,keys.Contains("workpiece_id")?"workpiece_id":"batch_id");
                    groups.Add(group.Length>0?group[0].ToString():null);
                }
                hasGroupMetadata=groups.Any(group=>!string.IsNullOrWhiteSpace(group));
                var plan=DatasetSplit.Create(groups,seed,trainPercent,validationPercent);
                var assignment=new string[samples.Length];
                foreach(var i in plan.Train)assignment[i]="训练集";
                foreach(var i in plan.Validation)assignment[i]="验证集";
                foreach(var i in plan.Test)assignment[i]="测试集";
                foreach (var i in Enumerable.Range(0, samples.Length))
                {
                    using var sample = samples.TupleSelect(i);
                    var keys = Keys(sample);
                    if (keys.Contains("segmentation_file_name")) tasks.Add("semantic");
                    else if (keys.Contains("mask")) tasks.Add("instance");
                    else if (keys.Contains("word")) tasks.Add("ocr");
                    else if (keys.Contains("bbox_label_id")) tasks.Add(keys.Contains("bbox_phi") ? "rotated" : "detection");
                    else if (keys.Contains("image_label_ids")) tasks.Add("multi");
                    else if (keys.Contains("image_label_id")) tasks.Add("classification");
                    else if (keys.Contains("anomaly_label")) tasks.Add("anomaly");
                    using var name = Value(sample, "image_file_name");
                    using var labels = Value(sample, "bbox_label_id");
                    count += labels.Length;
                    var filename = name.Length > 0 ? name[0].S : "";
                    var root = imageDir.Length > 0 ? imageDir[0].S : "";
                    var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, root, filename));
                    var missing = filename.Length == 0 || !File.Exists(path);
                    if (missing) issues.Add($"原图不存在：{path}");
                    using var segmentation = Value(sample, "segmentation_file_name");
                    if (segmentation.Length > 0) maskCount++;
                    var split=assignment[i];
                    splitCounts[split]++;
                    if (rows.Count < 100) rows.Add(new { name = filename, path, missing, split });
                }
                var task = tasks.Count == 1 ? tasks.Single() : "unknown";
                if (!hasGroupMetadata) issues.Add("无工件/批次元信息，预览按图像划分；正式训练前请核对相似样本是否跨集合。");
                if (splitCounts.Values.Any(value => value == 0)) issues.Add("存在空的数据集合，当前划分不能用于完整训练和评估。");
                if (tasks.Count != 1) issues.Add(tasks.Count == 0 ? "无法从样本标注识别任务。" : "样本包含多种任务标注，请检查数据集。");
                if (task is "ocr" or "anomaly") issues.Add("该数据需要结合模型或任务元信息区分训练组件。");
                return new { task, imageCount = samples.Length, classCount = classes.Length, annotationCount = count,
                    maskCount, splitCounts, splitStrategy = "优先按显式工件/批次ID分组，否则按图像；预览划分未改写源字典",
                    classes = Enumerable.Range(0, classes.Length).Select(i => classes[i].S).ToArray(), samples = rows, issues };
            }

        }
    }

    private static HashSet<string> Keys(HTuple dictionary)
    {
        HOperatorSet.GetDictParam(dictionary, "keys", new HTuple(), out var keys);
        using (keys) return Enumerable.Range(0, keys.Length).Select(i => keys[i].S).ToHashSet();
    }

    private static HTuple Value(HTuple dictionary, string key)
    {
        if (!Keys(dictionary).Contains(key)) return new HTuple();
        HOperatorSet.GetDictTuple(dictionary, key, out var value);
        return value;
    }
}
