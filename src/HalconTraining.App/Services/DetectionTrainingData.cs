using System.IO;
using System.Text.Json;

namespace HalconTraining.App.Services;

public sealed partial class DetectionActiveLearningStore
{
    public static DetectionReviewSession CloneSession(DetectionReviewSession session)=>
        JsonSerializer.Deserialize<DetectionReviewSession>(JsonSerializer.Serialize(session,Json),Json)!;

    /// <summary>Use the visible saved annotation layer; an intentionally empty draft overrides old predictions.</summary>
    public static IReadOnlyList<DetectionBox> TrainingBoxes(DetectionReviewImage image,int maxPredictions=300)=>
        image.HasDraft||image.Status=="approved"||image.ReviewedBoxes.Count>0 ? image.ReviewedBoxes :
        image.PredictedBoxes.OrderByDescending(b=>b.Confidence).Take(maxPredictions).ToArray();

    public static DetectionReviewSession AllTrainingScope(DetectionReviewSession session,bool combined=false,int maxPredictions=300)
    {
        var next=CloneSession(session);
        var removed=next.RemovedImagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        next.Images.RemoveAll(i=>removed.Contains(Path.GetFullPath(i.ImagePath)));
        if(combined)
        {
            // A background-only folder contributes images, not an object class with no examples.
            var ids=next.Images.SelectMany(i=>TrainingBoxes(i,maxPredictions).Select(b=>b.ClassId)).ToHashSet();
            next.Classes=next.Classes.Where(c=>ids.Contains(c.Id)).ToList();
            next.LastTrainedModelPath=session.CombinedModelPath;
            next.DatasetName=session.SourceDirectory is null?session.DatasetName:new DirectoryInfo(session.SourceDirectory).Name;
        }
        return next;
    }

    public static DetectionReviewSession TrainingSnapshot(DetectionReviewSession session,int maxPredictions=300)
    {
        var next=AllTrainingScope(session);
        if(next.Images.Count==0||next.Classes.Count==0)throw new InvalidOperationException("没有图像或有框目标类别；负样本需与有框样本合并训练。");
        foreach(var image in next.Images)
        {
            var boxes=TrainingBoxes(image,maxPredictions).ToList();
            image.TrainingLabelSource??=boxes.Count==0?"empty-box-negative":image.ReviewedBoxes.Count>0?"manual":"prediction";
            ValidateReviewedBoxes(image,boxes,next.Classes);
            if(image.FolderClassId is { } id&&boxes.Any(b=>b.ClassId!=id))throw new InvalidDataException("检测框类别与图像所属文件夹不一致："+image.ImagePath);
            image.ReviewedBoxes=boxes;
        }
        if(!next.Images.Any(i=>i.ReviewedBoxes.Count>0))throw new InvalidOperationException("全部图像都没有框，不能单独训练检测模型；请与有框样本合并训练。");
        return next;
    }
}
