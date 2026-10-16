using System.Text.Json;
using HalconDotNet;

namespace HalconTraining.Halcon.Training;

public static class SemanticEvaluator
{
    public static object Evaluate(string datasetFile, string runDirectory)
    {
        using var splitJson=JsonDocument.Parse(File.ReadAllText(Path.Combine(runDirectory,"split.json")));
        HOperatorSet.ReadDlModel(Path.Combine(runDirectory,"model.hdl"),out var model);
        HOperatorSet.ReadDict(Path.Combine(runDirectory,"preprocess.hdict"),new HTuple(),new HTuple(),out var preprocess);
        HOperatorSet.ReadDict(datasetFile,"convert_json_arrays_to","tuple",out var dataset);
        using(model) using(preprocess) using(dataset)
        {
            HOperatorSet.GetDictTuple(dataset,"samples",out var samples);
            HOperatorSet.GetDictTuple(dataset,"class_ids",out var classIds);
            HOperatorSet.GetDictTuple(dataset,"image_dir",out var imageRoot);
            HOperatorSet.GetDictTuple(dataset,"segmentation_dir",out var maskRoot);
            using(samples) using(classIds) using(imageRoot) using(maskRoot)
            {
                HOperatorSet.QueryAvailableDlDevices("runtime","gpu",out var devices);
                using(devices)
                {
                    if(devices.Length==0)throw new InvalidOperationException("无GPU可用于评估。");
                    using var device=devices.TupleSelect(0);
                    HOperatorSet.SetDlModelParam(model,"device",device);
                    HOperatorSet.SetDlModelParam(model,"batch_size",1);
                }
                using var engine=new HDevEngine();
                engine.SetProcedurePath(Path.Combine(Environment.GetEnvironmentVariable("HALCONROOT")!,"procedures","dl"));
                using var procedure=new HDevProcedure("preprocess_dl_samples");
                var reports=new Dictionary<string,object>();
                foreach(var subset in new[]{"validation","test"})
                {
                    var indices=splitJson.RootElement.GetProperty(subset).EnumerateArray().Select(x=>x.GetInt32()).ToArray();
                    var intersections=new double[classIds.Length];
                    var unions=new double[classIds.Length];
                    foreach(var index in indices)
                    {
                        using var source=samples.TupleSelect(index);
                        HOperatorSet.GetDictTuple(source,"image_file_name",out var imageName);
                        HOperatorSet.GetDictTuple(source,"segmentation_file_name",out var maskName);
                        using(imageName) using(maskName)
                        {
                            var baseDirectory=Path.GetDirectoryName(Path.GetFullPath(datasetFile))!;
                            HOperatorSet.ReadImage(out var image,Path.Combine(baseDirectory,imageRoot.S,imageName.S));
                            HOperatorSet.ReadImage(out var mask,Path.Combine(baseDirectory,maskRoot.S,maskName.S));
                            using(image) using(mask)
                            {
                                HOperatorSet.CreateDict(out var sample);
                                using(sample)
                                {
                                    HOperatorSet.SetDictObject(image,sample,"image");
                                    HOperatorSet.SetDictObject(mask,sample,"segmentation_image");
                                    using var call=new HDevProcedureCall(procedure);
                                    call.SetInputCtrlParamTuple("DLSampleBatch",sample);
                                    call.SetInputCtrlParamTuple("DLPreprocessParam",preprocess);
                                    call.Execute();
                                    HOperatorSet.ApplyDlModel(model,sample,new HTuple(),out var prediction);
                                    using(prediction)
                                    {
                                        HOperatorSet.GetDictObject(out var predicted,prediction,"segmentation_image");
                                        HOperatorSet.GetDictObject(out var truth,sample,"segmentation_image");
                                        using(predicted) using(truth)
                                        {
                                            for(var c=0;c<classIds.Length;c++)
                                            {
                                                HOperatorSet.Threshold(predicted,out var pr,classIds[c],classIds[c]);
                                                HOperatorSet.Threshold(truth,out var gt,classIds[c],classIds[c]);
                                                using(pr) using(gt)
                                                {
                                                    HOperatorSet.Intersection(pr,gt,out var intersection);
                                                    HOperatorSet.Union2(pr,gt,out var union);
                                                    using(intersection) using(union)
                                                    {
                                                        HOperatorSet.AreaCenter(intersection,out var ia,out var ir,out var ic);
                                                        HOperatorSet.AreaCenter(union,out var ua,out var ur,out var uc);
                                                        using(ia) using(ir) using(ic) using(ua) using(ur) using(uc)
                                                        {intersections[c]+=ia.D;unions[c]+=ua.D;}
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    var perClass=Enumerable.Range(0,classIds.Length).Select(c=>new {classId=classIds[c].I,iou=unions[c]>0?(double?)(intersections[c]/unions[c]):null}).ToArray();
                    reports[subset]=new{count=indices.Length,meanIoU=perClass.Where(c=>c.iou.HasValue).Average(c=>c.iou),perClass};
                }
                var report=new{status="reload_and_evaluation_passed",evaluatedAt=DateTimeOffset.Now,reports};
                File.WriteAllText(Path.Combine(runDirectory,"validation_report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
                return report;
            }
        }
    }
}
