using HalconDotNet;

namespace HalconTraining.Halcon.Training;

/// <summary>Validation only: no optimizer calls, no held-out test samples, no display operators.</summary>
internal static class TrainingValidation
{
    public sealed record Metrics(double MeanIoU,string PreviewImage,string PreviewMask,string TruthMask,int Count);
    public static Metrics Evaluate(HTuple model,HTuple preprocess,HDevProcedure procedure,
        IReadOnlyList<(string Image,string Mask)> records,int[] indices,int batchSize,int[] classIds,string outputDirectory,int epoch)
    {
        var intersections=new double[classIds.Length];var unions=new double[classIds.Length];
        var preview=Path.Combine(outputDirectory,"training-preview",$"epoch_{epoch:0000}");
        Directory.CreateDirectory(preview);
        for(var offset=0;offset<indices.Length;offset+=batchSize)
        {
            using var batch=new HTuple();
            for(var j=0;j<batchSize;j++)
            {
                var record=records[indices[Math.Min(offset+j,indices.Length-1)]];
                using var image=new HImage(record.Image);using var truth=new HImage(record.Mask);
                HOperatorSet.CreateDict(out var sample);
                using(sample){HOperatorSet.SetDictObject(image,sample,"image");HOperatorSet.SetDictObject(truth,sample,"segmentation_image");batch[j]=sample[0];}
            }
            using var call=new HDevProcedureCall(procedure);
            call.SetInputCtrlParamTuple("DLSampleBatch",batch);call.SetInputCtrlParamTuple("DLPreprocessParam",preprocess);call.Execute();
            HOperatorSet.ApplyDlModel(model,batch,new HTuple(),out var results);
            using(results)
                for(var j=0;j<Math.Min(batchSize,indices.Length-offset);j++)
                {
                    using var sample=batch.TupleSelect(j);using var result=results.TupleSelect(j);
                    HOperatorSet.GetDictObject(out var prediction,result,"segmentation_image");
                    HOperatorSet.GetDictObject(out var groundTruth,sample,"segmentation_image");
                    using(prediction)using(groundTruth)
                    {
                        for(var c=0;c<classIds.Length;c++)
                        {
                            HOperatorSet.Threshold(prediction,out var predictedRegion,classIds[c],classIds[c]);
                            HOperatorSet.Threshold(groundTruth,out var truthRegion,classIds[c],classIds[c]);
                            using(predictedRegion)using(truthRegion)
                            {
                                HOperatorSet.Intersection(predictedRegion,truthRegion,out var intersection);
                                HOperatorSet.Union2(predictedRegion,truthRegion,out var union);
                                using(intersection)using(union)
                                {
                                    HOperatorSet.AreaCenter(intersection,out var areaIntersection,out var ir,out var ic);
                                    HOperatorSet.AreaCenter(union,out var areaUnion,out var ur,out var uc);
                                    using(areaIntersection)using(ir)using(ic)using(areaUnion)using(ur)using(uc){intersections[c]+=areaIntersection.D;unions[c]+=areaUnion.D;}
                                }
                            }
                        }
                        if(offset==0&&j==0)
                        {
                            var record=records[indices[0]];using var original=new HImage(record.Image);using var truth=new HImage(record.Mask);
                            original.GetImageSize(out int width,out int height);
                            HOperatorSet.ZoomImageSize(prediction,out var mapped,width,height,"nearest_neighbor");
                            using(mapped)
                            {
                                HOperatorSet.ConvertImageType(mapped,out var mask,"byte");
                                using(mask)HOperatorSet.WriteImage(mask,"png",0,Path.Combine(preview,"prediction.png"));
                            }
                            original.WriteImage("png",0,Path.Combine(preview,"original.png"));truth.WriteImage("png",0,Path.Combine(preview,"truth.png"));
                        }
                    }
                }
        }
        var valid=Enumerable.Range(0,classIds.Length).Where(c=>unions[c]>0).ToArray();
        if(valid.Length==0)throw new InvalidOperationException("验证集没有可计算IoU的类别。");
        return new(valid.Average(c=>intersections[c]/unions[c]),Path.Combine(preview,"original.png"),Path.Combine(preview,"prediction.png"),Path.Combine(preview,"truth.png"),indices.Length);
    }
}
