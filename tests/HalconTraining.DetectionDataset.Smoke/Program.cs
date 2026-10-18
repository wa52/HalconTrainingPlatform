using System.Security.Cryptography;
using System.Text.Json;
using HalconDotNet;
using HalconTraining.App.Services;
using HalconTraining.Halcon.Datasets;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented=true };
string? Option(string key) { var i=Array.IndexOf(args,key); return i>=0&&i+1<args.Length?args[i+1]:null; }
void Check(bool value,string message) { if(!value)throw new InvalidDataException(message); }
string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

if (Option("--session") is { } sessionFile)
{
    var workspace=Option("--workspace") ?? throw new ArgumentException("缺少--workspace");
    var originalHash=Hash(sessionFile);
    var session=JsonSerializer.Deserialize<DetectionReviewSession>(File.ReadAllText(sessionFile),json)!;
    var store=new DetectionActiveLearningStore(workspace);
    var results=new List<object>();
    var scopes=args.Contains("--combined") ? new[]{DetectionActiveLearningStore.CombinedApprovedScope(session)}
        : session.Classes.Select(c=>DetectionActiveLearningStore.FolderScope(session,c.Id)).ToArray();
    foreach(var scope in scopes)
    {
        if(!scope.Images.Any(i=>i.Status=="approved"))continue;
        string file; object result;
        if(Option("--verify-dataset") is { } existing)
        {
            file=Path.GetFullPath(existing);
            result=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(file,"export.json")));
        }
        else
        {
            var folder=await store.ExportApprovedCocoAsync(scope);
            var name=string.Concat(scope.DatasetName.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
            file=Path.Combine(folder,name+".hdict");
            result=DetectionDatasetFile.Export(Path.Combine(folder,"annotations.json"),file);
        }
        var loaded=DetectionDatasetFile.Read(file);
        using(var inspection=JsonDocument.Parse(JsonSerializer.Serialize(DatasetInspector.Inspect(file))))
        {
            var counts=inspection.RootElement.GetProperty("splitCounts");
            Check(counts.GetProperty("训练集").GetInt32()==loaded.Images.Count(i=>i.Split=="train") &&
                counts.GetProperty("验证集").GetInt32()==loaded.Images.Count(i=>i.Split=="validation") &&
                counts.GetProperty("测试集").GetInt32()==loaded.Images.Count(i=>i.Split=="test"),"数据集浏览器没有保留已有划分。");
        }
        Check(loaded.Classes.SequenceEqual(scope.Classes.Select(c=>(c.Id,c.Name))),"类别映射发生变化。");
        var approved=scope.Images.Where(i=>i.Status=="approved").ToArray();
        Check(loaded.Images.Length==approved.Length,"已确认图片数量不一致。");
        for(var i=0;i<approved.Length;i++)
        {
            Check(Hash(approved[i].ImagePath)==Hash(loaded.Images[i].Path),"原图副本发生改变。");
            var expected=approved[i].ReviewedBoxes.Select(b=>new DetectionDatasetBox(b.ClassId,b.Col1,b.Row1,b.Col2,b.Row2));
            Check(expected.SequenceEqual(loaded.Images[i].Boxes),"标注坐标发生改变。");
        }
        results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
    }
    Check(Hash(sessionFile)==originalHash,"原始审核会话发生改变。");
    var report=Option("--report")??Path.Combine(workspace,"active-learning","hdict-export-verification.json");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
    await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new {passed=true,sourceUnchanged=true,datasets=results},json));
    return;
}

var root=Path.Combine(Path.GetTempPath(),"halcon-hdict-smoke-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var image=new HImage(); image.GenImageConst("byte",100,80);
    var original=Path.Combine(root,"original.png"); image.WriteImage("png",0,original);
    var session=new DetectionReviewSession {Id="fixture",DatasetName="划痕",Classes=[new(7,"划痕")],Images=[]};
    for(var i=0;i<10;i++)session.Images.Add(new DetectionReviewImage {ImagePath=original,Width=100,Height=80,FolderClassId=7,
        Status=i<8?"approved":"pending",HasDraft=true,ReviewedBoxes=i==0?[]:[new(7,.8,10.25,12.5,75.75,65.5)]});
    var store=new DetectionActiveLearningStore(root);
    var folder=await store.ExportApprovedCocoAsync(session);
    var file=Path.Combine(folder,"划痕.hdict");
    DetectionDatasetFile.Export(Path.Combine(folder,"annotations.json"),file);
    var data=DetectionDatasetFile.Read(file);
    Check(data.Images.Length==8&&data.Images.Sum(i=>i.Boxes.Length)==7,"负样本、未确认或草稿筛选错误。");
    Check(data.Images[1].Boxes[0]==new DetectionDatasetBox(7,10.25,12.5,75.75,65.5),"小数坐标精度丢失。");
    Check(data.Images.Count(i=>i.Split=="train")==6&&data.Images.Count(i=>i.Split=="validation")==1&&data.Images.Count(i=>i.Split=="test")==1,"自动划分不正确。");
    session.Classes.Add(new(9,"缺口"));session.Classes.Add(new(10,"未确认类别"));
    for(var i=0;i<8;i++)session.Images.Add(new DetectionReviewImage{ImagePath=original,Width=100,Height=80,FolderClassId=9,
        Status="approved",HasDraft=true,ReviewedBoxes=[new(9,1,5,6,70,60)]});
    session.Images.Add(new DetectionReviewImage{ImagePath=original,Width=100,Height=80,FolderClassId=10,
        Status="pending",HasDraft=true,ReviewedBoxes=[new(10,1,1,2,3,4)]});
    var combined=DetectionActiveLearningStore.CombinedApprovedScope(session);
    Check(combined.Images.Count==16&&combined.Classes.Select(c=>c.Id).SequenceEqual(new[]{7,9}),"未确认图像或类别混入合并数据集。");
    var combinedFolder=await store.ExportApprovedCocoAsync(combined);
    var combinedFile=Path.Combine(combinedFolder,"combined.hdict");
    DetectionDatasetFile.Export(Path.Combine(combinedFolder,"annotations.json"),combinedFile);
    var combinedData=DetectionDatasetFile.Read(combinedFile);
    foreach(var id in new[]{7,9})Check(combinedData.Images.Where(i=>i.Boxes.Any(b=>b.ClassId==id)).Select(i=>i.Split).Distinct().Count()==3,
        "合并数据集划分未覆盖全部类别。");
    var moved=Path.Combine(root,"moved"); Directory.Move(folder,moved);
    Check(DetectionDatasetFile.Read(Path.Combine(moved,"划痕.hdict")).Images.All(i=>File.Exists(i.Path)),"移动后的相对图像路径失效。");
    var coco=Path.Combine(moved,"annotations.json");
    File.WriteAllText(coco,File.ReadAllText(coco).Replace("10.25","-10.25"));
    var rejected=false; try{DetectionDatasetFile.Export(coco,Path.Combine(moved,"invalid.hdict"));}catch(InvalidDataException){rejected=true;}
    Check(rejected&&!File.Exists(Path.Combine(moved,"invalid.hdict")),"越界标注未被拒绝。");
    Console.WriteLine("PASS: HALCON二进制读写、人工审核筛选、空框负样本、小数坐标、类别隔离、自动划分、整体移动和越界拒绝。");
}
finally
{
    var target=Path.GetFullPath(root);
    var parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    if(!string.Equals(Path.GetDirectoryName(target),parent,StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(target).StartsWith("halcon-hdict-smoke-",StringComparison.Ordinal))
        throw new InvalidOperationException("拒绝清理预期临时目录以外的路径。");
    Directory.Delete(target,true);
}
