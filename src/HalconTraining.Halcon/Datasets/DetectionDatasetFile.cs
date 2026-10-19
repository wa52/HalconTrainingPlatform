using System.Text.Json;
using HalconDotNet;
using HalconTraining.Domain.Training;

namespace HalconTraining.Halcon.Datasets;

public sealed record DetectionDatasetBox(int ClassId, double Col1, double Row1, double Col2, double Row2);
public sealed record DetectionDatasetImage(int Id, string Path, int Width, int Height, DetectionDatasetBox[] Boxes, string Split = "train");
public sealed record DetectionDatasetContents((int Id, string Name)[] Classes, DetectionDatasetImage[] Images);

/// <summary>Portable HALCON DLDataset dictionaries. Only called inside a one-job worker.</summary>
public static class DetectionDatasetFile
{
    public static DetectionDatasetContents ReadCoco(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        var classes = root.GetProperty("categories").EnumerateArray().Select(c =>
            (c.GetProperty("id").GetInt32(), c.GetProperty("name").GetString() ?? "")).ToArray();
        var boxes = root.GetProperty("annotations").EnumerateArray().GroupBy(a => a.GetProperty("image_id").GetInt32())
            .ToDictionary(g => g.Key, g => g.Select(a =>
            {
                var r = a.GetProperty("bbox").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (r.Length != 4) throw new InvalidDataException("COCO bbox 必须为 x,y,w,h。");
                if (a.TryGetProperty("halcon_bbox",out var exact))
                {
                    var h=exact.EnumerateArray().Select(v=>v.GetDouble()).ToArray();
                    if(h.Length!=4 || Math.Abs(h[1]-r[0])>1e-8 || Math.Abs(h[0]-r[1])>1e-8 ||
                        Math.Abs((h[3]-h[1])-r[2])>1e-8 || Math.Abs((h[2]-h[0])-r[3])>1e-8)
                        throw new InvalidDataException("HALCON原始坐标与COCO标注不一致。");
                    return new DetectionDatasetBox(a.GetProperty("category_id").GetInt32(),h[1],h[0],h[3],h[2]);
                }
                return new DetectionDatasetBox(a.GetProperty("category_id").GetInt32(), r[0], r[1], r[0]+r[2], r[1]+r[3]);
            }).ToArray());
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "images");
        var images = root.GetProperty("images").EnumerateArray().Select(i =>
        {
            var name = i.GetProperty("file_name").GetString() ?? "";
            if (name.Length == 0 || name != Path.GetFileName(name)) throw new InvalidDataException("快照图像必须位于 images 目录。");
            var id = i.GetProperty("id").GetInt32();
            return new DetectionDatasetImage(id, Path.Combine(directory, name), i.GetProperty("width").GetInt32(),
                i.GetProperty("height").GetInt32(), boxes.GetValueOrDefault(id, []));
        }).ToArray();
        if (boxes.Keys.Except(images.Select(i => i.Id)).Any()) throw new InvalidDataException("标注引用了不存在的图像。");
        return Validate(new(classes, images));
    }

    public static DetectionDatasetContents Read(string file)
    {
        HOperatorSet.ReadDict(file, new HTuple(), new HTuple(), out var dataset);
        using (dataset)
        using (var ids = Get(dataset, "class_ids"))
        using (var names = Get(dataset, "class_names"))
        using (var imageDir = Get(dataset, "image_dir"))
        using (var samples = Get(dataset, "samples"))
        {
            if (ids.Length != names.Length) throw new InvalidDataException("HALCON 类别 ID 与名称数量不一致。");
            var classes = Enumerable.Range(0, ids.Length).Select(i => (ids[i].I, names[i].S)).ToArray();
            var images = new List<DetectionDatasetImage>();
            for (var i = 0; i < samples.Length; i++)
            {
                using var sample = samples.TupleSelect(i);
                using var id = Get(sample, "image_id");
                using var name = Get(sample, "image_file_name");
                using var labels = Get(sample, "bbox_label_id");
                using var r1 = Get(sample, "bbox_row1"); using var c1 = Get(sample, "bbox_col1");
                using var r2 = Get(sample, "bbox_row2"); using var c2 = Get(sample, "bbox_col2");
                using var split = Get(sample, "split");
                if (new[] {r1.Length,c1.Length,r2.Length,c2.Length}.Any(n => n != labels.Length))
                    throw new InvalidDataException("HALCON 检测框坐标与标签数量不一致。");
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, imageDir.S, name.S));
                using var image = new HImage(path);
                image.GetImageSize(out int width, out int height);
                images.Add(new(id.I, path, width, height, Enumerable.Range(0, labels.Length).Select(j =>
                    new DetectionDatasetBox(labels[j].I,c1[j].D,r1[j].D,c2[j].D,r2[j].D)).ToArray(), split.S));
            }
            return Validate(new(classes, images.ToArray()));
        }
    }

    public static object Export(string cocoFile, string outputFile, int seed = 42, double trainPercent = 70, double validationPercent = 15)
    {
        var data = ReadCoco(cocoFile);
        outputFile = Path.GetFullPath(outputFile);
        if (Path.GetExtension(outputFile).ToLowerInvariant() != ".hdict") throw new ArgumentException("输出文件必须为 .hdict。");
        if (File.Exists(outputFile)) throw new IOException("目标数据集已存在，请选择新文件名。");
        var directory = Path.GetDirectoryName(outputFile)!;
        // Images are already copied by the reviewed-label snapshot exporter. Keep the whole folder portable.
        if (data.Images.Any(i => !string.Equals(Path.GetDirectoryName(i.Path), Path.Combine(directory,"images"), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(".hdict 必须与快照 images 目录放在同一文件夹。");
        // Stratify by the actual label set. For folder labels this keeps every class in each split.
        var partitions = data.Images.Select((image,index)=>(image,index))
            .GroupBy(x=>string.Join(",",x.image.Boxes.Select(b=>b.ClassId).Distinct().OrderBy(id=>id)))
            .Select(group=>
            {
                var indices=group.Select(x=>x.index).ToArray();
                var split=DatasetSplit.Create(new string?[indices.Length],seed,trainPercent,validationPercent);
                return new DatasetSplit(seed,split.Train.Select(i=>indices[i]).ToArray(),
                    split.Validation.Select(i=>indices[i]).ToArray(),split.Test.Select(i=>indices[i]).ToArray());
            }).ToArray();
        var plan = new DatasetSplit(seed,partitions.SelectMany(p=>p.Train).ToArray(),
            partitions.SelectMany(p=>p.Validation).ToArray(),partitions.SelectMany(p=>p.Test).ToArray());
        var splits = new string[data.Images.Length];
        foreach (var i in plan.Train) splits[i] = "train";
        foreach (var i in plan.Validation) splits[i] = "validation";
        foreach (var i in plan.Test) splits[i] = "test";
        HOperatorSet.CreateDict(out var dataset);
        using (dataset)
        using (var samples = new HTuple())
        {
            HOperatorSet.SetDictTuple(dataset,"class_ids",new HTuple(data.Classes.Select(c=>c.Id).ToArray()));
            HOperatorSet.SetDictTuple(dataset,"class_names",new HTuple(data.Classes.Select(c=>c.Name).ToArray()));
            HOperatorSet.SetDictTuple(dataset,"image_dir","images");
            for (var i = 0; i < data.Images.Length; i++)
            {
                var item = data.Images[i];
                using var image = new HImage(item.Path);
                image.GetImageSize(out int width,out int height);
                if (width != item.Width || height != item.Height) throw new InvalidDataException("原图尺寸与已审核标注不一致："+item.Path);
                HOperatorSet.CreateDict(out var sample);
                using (sample)
                {
                    HOperatorSet.SetDictTuple(sample,"image_id",item.Id);
                    HOperatorSet.SetDictTuple(sample,"image_file_name",Path.GetFileName(item.Path));
                    HOperatorSet.SetDictTuple(sample,"split",splits[i]);
                    SetBoxes(sample,item.Boxes);
                    samples[i] = sample[0];
                }
            }
            HOperatorSet.SetDictTuple(dataset,"samples",samples);
            var temporary = outputFile+"."+Guid.NewGuid().ToString("N")+".tmp.hdict";
            try
            {
                HOperatorSet.WriteDict(dataset,temporary,new HTuple(),new HTuple());
                var loaded = Read(temporary);
                if (!data.Classes.SequenceEqual(loaded.Classes) || loaded.Images.Length != data.Images.Length ||
                    loaded.Images.Where((item,i) => item.Id != data.Images[i].Id || item.Path != data.Images[i].Path ||
                        item.Width != data.Images[i].Width || item.Height != data.Images[i].Height || item.Split != splits[i] ||
                        !item.Boxes.SequenceEqual(data.Images[i].Boxes)).Any())
                    throw new InvalidDataException("HALCON 数据集写入后复读校验失败。");
                File.Move(temporary,outputFile,false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var result = new { status="exported",datasetFile=outputFile,task="detection",images=data.Images.Length,
            boxes=data.Images.Sum(i=>i.Boxes.Length),negativeImages=data.Images.Count(i=>i.Boxes.Length==0),classes=data.Classes.Select(c=>new {id=c.Id,name=c.Name}),
            splitCounts=new {train=plan.Train.Length,validation=plan.Validation.Length,test=plan.Test.Length},seed,trainPercent,validationPercent,
            roundTripVerified=true,note="保留全部快照图像；无框为负样本。按标注类别分层划分，移动时请保留 images 子目录。" };
        File.WriteAllText(Path.ChangeExtension(outputFile,"export.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions {WriteIndented=true}));
        return result;
    }

    public static void SetBoxes(HTuple sample, IReadOnlyList<DetectionDatasetBox> boxes)
    {
        HOperatorSet.SetDictTuple(sample,"bbox_label_id",new HTuple(boxes.Select(b=>b.ClassId).ToArray()));
        HOperatorSet.SetDictTuple(sample,"bbox_row1",new HTuple(boxes.Select(b=>b.Row1).ToArray()));
        HOperatorSet.SetDictTuple(sample,"bbox_col1",new HTuple(boxes.Select(b=>b.Col1).ToArray()));
        HOperatorSet.SetDictTuple(sample,"bbox_row2",new HTuple(boxes.Select(b=>b.Row2).ToArray()));
        HOperatorSet.SetDictTuple(sample,"bbox_col2",new HTuple(boxes.Select(b=>b.Col2).ToArray()));
    }

    private static HTuple Get(HTuple dictionary,string key)
    { HOperatorSet.GetDictTuple(dictionary,key,out var value); return value; }

    private static DetectionDatasetContents Validate(DetectionDatasetContents data)
    {
        if (data.Classes.Length == 0 || data.Classes.Any(c=>c.Id<0 || string.IsNullOrWhiteSpace(c.Name)) ||
            data.Classes.Select(c=>c.Id).Distinct().Count()!=data.Classes.Length || data.Images.Length==0 ||
            data.Images.Select(i=>i.Id).Distinct().Count()!=data.Images.Length)
            throw new InvalidDataException("数据集图像或类别无效。");
        var ids = data.Classes.Select(c=>c.Id).ToHashSet();
        foreach (var i in data.Images)
        {
            if (!File.Exists(i.Path)) throw new FileNotFoundException("数据集原图不存在。",i.Path);
            if (i.Width<=0 || i.Height<=0 || i.Split is not ("train" or "validation" or "test") || i.Boxes.Any(b=>
                !ids.Contains(b.ClassId) || new[]{b.Col1,b.Row1,b.Col2,b.Row2}.Any(v=>!double.IsFinite(v)) ||
                b.Col1<0 || b.Row1<0 || b.Col2>i.Width || b.Row2>i.Height || b.Col2<=b.Col1 || b.Row2<=b.Row1))
                throw new InvalidDataException("数据集检测框、类别、划分或图像尺寸无效："+i.Path);
        }
        return data;
    }
}
