using HalconDotNet;

namespace HalconTraining.Halcon.Training;

/// <summary>Native metadata is authoritative; JSON manifests alone cannot authorize weight reuse.</summary>
public static class DetectionModelInitializer
{
    public static void ValidatePreprocess(HTuple model,HTuple preprocess)
    {
        using var expected=new HTuple(new[]{"image_width","image_height","image_num_channels","image_range_min","image_range_max"});
        foreach(var key in expected.ToSArr())
        {
            HOperatorSet.GetDlModelParam(model,key,out var modelValue);
            HOperatorSet.GetDictTuple(preprocess,key,out var preprocessingValue);
            using(modelValue)using(preprocessingValue)
                if(!modelValue.ToDArr().SequenceEqual(preprocessingValue.ToDArr()))
                    throw new InvalidDataException("续训预处理参数与模型不一致："+key);
        }
        HOperatorSet.GetDictTuple(preprocess,"model_type",out var type);
        using(type)if(type.S!="detection")throw new InvalidDataException("续训预处理必须为 detection。");
        HOperatorSet.GetDictTuple(preprocess,"instance_type",out var instance);
        using(instance)if(instance.S!="rectangle1")throw new InvalidDataException("续训预处理必须为水平框 rectangle1。");
        HOperatorSet.GetDictTuple(preprocess,"domain_handling",out var domain);
        using(domain)if(domain.S!="full_domain")throw new InvalidDataException("当前检测训练仅支持 full_domain 预处理。");
    }

    public static HTuple Create(string backboneFile,string? initialModelFile,(int Id,string Name)[] classes,
        int width,int height)
    {
        HTuple model;
        if(initialModelFile is not null)
        {
            HOperatorSet.ReadDlModel(initialModelFile,out model);
            try { Validate(model,classes,width,height); }
            catch {model.Dispose();throw;}
        }
        else
        {
            HOperatorSet.CreateDict(out var parameters);
            using(parameters)
            {
                HOperatorSet.SetDictTuple(parameters,"image_width",width);
                HOperatorSet.SetDictTuple(parameters,"image_height",height);
                HOperatorSet.CreateDlModelDetection(backboneFile,classes.Length,parameters,out model);
            }
        }
        try
        {
            if(initialModelFile is null)
            {
                using var ids=new HTuple(classes.Select(c=>c.Id).ToArray());
                using var names=new HTuple(classes.Select(c=>c.Name).ToArray());
                HOperatorSet.SetDlModelParam(model,"class_ids",ids);
                HOperatorSet.SetDlModelParam(model,"class_names",names);
            }
            return model;
        }
        catch {model.Dispose();throw;}
    }

    public static void Validate(HTuple model,(int Id,string Name)[] classes,int width,int height)
    {
        HTuple Param(string name){HOperatorSet.GetDlModelParam(model,name,out var value);return value;}
        using var type=Param("type");
        if(type.S!="detection")throw new InvalidDataException("续训模型必须是 HALCON 目标检测模型。");
        using var instance=Param("instance_type");using var w=Param("image_width");using var h=Param("image_height");
        using var ids=Param("class_ids");using var names=Param("class_names");
        if(instance.S!="rectangle1"||w.I!=width||h.I!=height || !ids.ToIArr().SequenceEqual(classes.Select(c=>c.Id)) ||
            !names.ToSArr().SequenceEqual(classes.Select(c=>c.Name)))
            throw new InvalidDataException("续训模型的框类型、图像尺寸或类别 ID/名称与当前数据集不一致；未修改旧模型。");
    }
}
