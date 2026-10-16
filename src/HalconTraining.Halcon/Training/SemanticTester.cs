using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HalconDotNet;

namespace HalconTraining.Halcon.Training;

/// <summary>One-image inference in an isolated worker. Masks are mapped back to original pixels.</summary>
public static class SemanticTester
{
    public static void SaveOverlay(string originalFile,string maskFile,string output,double opacity)
    {
        if(!double.IsFinite(opacity)||opacity<0||opacity>1)throw new ArgumentException("透明度必须在0到1之间。");
        using var original=new HImage(originalFile);using var mask=new HImage(maskFile);
        using var channels=original.CountChannels();
        using var rgb=channels.I==1?original.Compose3(original,original):original.CopyImage();
        using var region=mask.Threshold(1d,255d);
        rgb.GetImageSize(out int width,out int height);
        mask.GetImageSize(out int maskWidth,out int maskHeight);
        if(width!=maskWidth||height!=maskHeight)throw new InvalidOperationException("预测掩膜和原图尺寸不一致。");
        using var zero=new HImage();zero.GenImageConst("byte",width,height);
        using var green=zero.ScaleImage(0,225*opacity);using var blue=zero.ScaleImage(0,255*opacity);
        using var tint=zero.Compose3(green,blue);using var faded=rgb.ScaleImage(1-opacity,0);
        using var mixed=faded.AddImage(tint,1d,0d);using var patch=mixed.ReduceDomain(region);
        using var overlay=patch.PaintGray(rgb);
        overlay.WriteImage("png",0,output);
    }
    public static object Test(string imageFile, string modelDirectory, string outputDirectory)
    {
        var total = Stopwatch.StartNew();
        Directory.CreateDirectory(outputDirectory);
        var modelFile = Path.Combine(modelDirectory,"model.hdl");
        HOperatorSet.ReadDlModel(modelFile,out var model);
        HOperatorSet.ReadDict(Path.Combine(modelDirectory,"preprocess.hdict"),new HTuple(),new HTuple(),out var preprocess);
        HOperatorSet.ReadImage(out var image,imageFile);
        using(model) using(preprocess) using(image)
        {
            HOperatorSet.GetDlModelParam(model,"type",out var modelType);
            using(modelType) if(modelType.S!="segmentation") throw new InvalidOperationException("测试入口仅支持语义分割模型。");
            HOperatorSet.GetDlModelParam(model,"class_ids",out var classIds);
            HOperatorSet.GetImageSize(image,out var originalWidth,out var originalHeight);
            using(classIds) using(originalWidth) using(originalHeight)
            {
                if(classIds.ToIArr().Any(id=>id<0||id>255))throw new InvalidOperationException("当前可视化支持0到255的类别ID。");
                HOperatorSet.QueryAvailableDlDevices("runtime","gpu",out var devices);
                using(devices)
                {
                    if(devices.Length==0)throw new InvalidOperationException("未发现可用GPU。");
                    using var device=devices.TupleSelect(0);
                    HOperatorSet.SetDlModelParam(model,"device",device);
                    HOperatorSet.SetDlModelParam(model,"batch_size",1);
                }
                using var engine=new HDevEngine();
                engine.SetProcedurePath(Path.Combine(Environment.GetEnvironmentVariable("HALCONROOT")!,"procedures","dl"));
                using var procedure=new HDevProcedure("preprocess_dl_samples");
                HOperatorSet.CreateDict(out var sample);
                using(sample)
                {
                    HOperatorSet.SetDictObject(image,sample,"image");
                    using var call=new HDevProcedureCall(procedure);
                    call.SetInputCtrlParamTuple("DLSampleBatch",sample);
                    call.SetInputCtrlParamTuple("DLPreprocessParam",preprocess);
                    call.Execute();
                    var inference=Stopwatch.StartNew();
                    HOperatorSet.ApplyDlModel(model,sample,new HTuple(),out var prediction);
                    inference.Stop();
                    using(prediction)
                    {
                        HOperatorSet.GetDictObject(out var labels,prediction,"segmentation_image");
                        using(labels)
                        {
                            // The current archive uses full_domain preprocessing without crop/padding.
                            HOperatorSet.GetDictTuple(preprocess,"domain_handling",out var domain);
                            using(domain) if(domain.S!="full_domain")throw new InvalidOperationException("当前坐标还原只支持full_domain预处理。");
                            HOperatorSet.ZoomImageSize(labels,out var originalLabels,originalWidth,originalHeight,"nearest_neighbor");
                            using(originalLabels)
                            {
                                HOperatorSet.ConvertImageType(originalLabels,out var byteLabels,"byte");
                                using(byteLabels) HOperatorSet.WriteImage(byteLabels,"png",0,Path.Combine(outputDirectory,"mask.png"));
                                HOperatorSet.WriteImage(image,"png",0,Path.Combine(outputDirectory,"original.png"));
                                var areas=new List<object>();
                                for(var c=0;c<classIds.Length;c++)
                                {
                                    HOperatorSet.Threshold(originalLabels,out var region,classIds[c],classIds[c]);
                                    using(region)
                                    {
                                        HOperatorSet.AreaCenter(region,out var area,out var row,out var col);
                                        using(area) using(row) using(col) areas.Add(new {classId=classIds[c].I,pixels=area.D,ratio=area.D/(originalWidth.I*(double)originalHeight.I)});
                                    }
                                }
                                var report=new {file=Path.GetFileName(imageFile),width=originalWidth.I,height=originalHeight.I,
                                    inferenceMs=inference.Elapsed.TotalMilliseconds,totalMs=total.Elapsed.TotalMilliseconds,
                                    modelFile,modelSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelFile))),areas,
                                    outputDirectory,maskFile="mask.png",originalFile="original.png"};
                                File.WriteAllText(Path.Combine(outputDirectory,"result.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
                                return report;
                            }
                        }
                    }
                }
            }
        }
    }
}
