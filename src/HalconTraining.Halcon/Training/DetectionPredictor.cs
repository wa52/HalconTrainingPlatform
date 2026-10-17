using System.Security.Cryptography;
using System.Text.Json;
using HalconDotNet;

namespace HalconTraining.Halcon.Training;

/// <summary>
/// Batch rectangle1 detection inference from a trained HALCON .hdl model.
/// Keeps HALCON and GPU handles inside one short-lived Worker process.
/// </summary>
public static class DetectionPredictor
{
    public static object Predict(string modelFile, string imageListJson, string outputJson, Action<object> report)
    {
        if (!File.Exists(modelFile)) throw new FileNotFoundException("目标检测模型不存在。", modelFile);
        var imagePaths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(imageListJson))
            ?? throw new InvalidDataException("没有待预测的图像。");
        if (imagePaths.Length == 0) throw new InvalidDataException("请先选择未标注的图片。");
        if (imagePaths.Length != imagePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidDataException("预测图片列表包含重复路径。");

        HOperatorSet.ReadDlModel(modelFile, out var model);
        using (model)
        {
            HOperatorSet.GetDlModelParam(model, "type", out var type);
            using (type)
                if (type.S != "detection") throw new InvalidDataException("选择的不是 HALCON 目标检测模型。");
            HOperatorSet.GetDlModelParam(model, "instance_type", out var instanceType);
            using (instanceType)
                if (instanceType.S != "rectangle1")
                    throw new NotSupportedException("当前主动学习仅支持水平矩形框 rectangle1 检测模型。");

            HOperatorSet.GetDlModelParam(model, "class_ids", out var ids);
            HOperatorSet.GetDlModelParam(model, "class_names", out var names);
            HOperatorSet.GetDlModelParam(model, "image_width", out var modelWidth);
            HOperatorSet.GetDlModelParam(model, "image_height", out var modelHeight);
            using (ids) using (names) using (modelWidth) using (modelHeight)
            {
                var classIds = ids.ToIArr();
                if (classIds.Length == 0 || modelWidth.I <= 0 || modelHeight.I <= 0)
                    throw new InvalidDataException("模型缺少类别ID或图像尺寸参数。");
                var classNames = names.Length == classIds.Length ? names.ToSArr() :
                    classIds.Select(id => $"类别{id}").ToArray();
                HOperatorSet.QueryAvailableDlDevices("runtime", "gpu", out var devices);
                using (devices)
                {
                    if (devices.Length == 0) throw new InvalidOperationException("HALCON 未发现可用的 GPU 推理设备。");
                    using var device = devices.TupleSelect(0);
                    HOperatorSet.SetDlModelParam(model, "device", device);
                    HOperatorSet.SetDlModelParam(model, "batch_size", 1);
                    HOperatorSet.SetDlModelParam(model, "runtime_init", "immediately");
                }

                using var engine = new HDevEngine();
                engine.SetProcedurePath(Path.Combine(
                    Environment.GetEnvironmentVariable("HALCONROOT") ?? throw new InvalidOperationException("未设置 HALCONROOT。"),
                    "procedures", "dl"));
                using var procedure = new HDevProcedure("preprocess_dl_samples");
                var preprocessPath = Path.Combine(Path.GetDirectoryName(modelFile)!, "preprocess.hdict");
                if (!File.Exists(preprocessPath))
                    throw new FileNotFoundException("已有检测模型必须提供与训练一致的 preprocess.hdict，请将其放在 .hdl 同目录。", preprocessPath);
                HOperatorSet.ReadDict(preprocessPath, new HTuple(), new HTuple(), out var preprocess);
                using (preprocess)
                {
                    HOperatorSet.GetDictTuple(preprocess, "domain_handling", out var handling);
                    using (handling)
                        if (handling.S != "full_domain")
                            throw new NotSupportedException("坐标映射目前只支持 full_domain 预处理。");

                    HOperatorSet.GetDictTuple(preprocess, "image_width", out var prepWidth);
                    HOperatorSet.GetDictTuple(preprocess, "image_height", out var prepHeight);
                    using (prepWidth) using (prepHeight)
                    {
                        var results = new List<object>();
                        var errors = new List<object>();
                        for (var i = 0; i < imagePaths.Length; i++)
                        {
                            var path = Path.GetFullPath(imagePaths[i]);
                            try
                            {
                                using var image = new HImage(path);
                                image.GetImageSize(out int width, out int height);
                                HOperatorSet.CreateDict(out var sample);
                                using (sample)
                                {
                                    HOperatorSet.SetDictObject(image, sample, "image");
                                    using (var call = new HDevProcedureCall(procedure))
                                    {
                                        call.SetInputCtrlParamTuple("DLSampleBatch", sample);
                                        call.SetInputCtrlParamTuple("DLPreprocessParam", preprocess);
                                        call.Execute();
                                    }
                                    HOperatorSet.ApplyDlModel(model, sample, new HTuple(), out var prediction);
                                    using (prediction)
                                    {
                                        var rows1 = ReadDoubles(prediction, "bbox_row1");
                                        var cols1 = ReadDoubles(prediction, "bbox_col1");
                                        var rows2 = ReadDoubles(prediction, "bbox_row2");
                                        var cols2 = ReadDoubles(prediction, "bbox_col2");
                                        var classes = ReadInts(prediction, "bbox_class_id");
                                        var scores = ReadDoubles(prediction, "bbox_confidence");
                                        if (new[] { cols1.Length, rows2.Length, cols2.Length, classes.Length, scores.Length }
                                            .Any(count => count != rows1.Length))
                                            throw new InvalidDataException("检测模型返回的框字段数量不匹配。");
                                        var boxes = new List<object>();
                                        for (var b = 0; b < rows1.Length; b++)
                                        {
                                            var left = Math.Clamp(cols1[b] * width / prepWidth.D, 0, width);
                                            var top = Math.Clamp(rows1[b] * height / prepHeight.D, 0, height);
                                            var right = Math.Clamp(cols2[b] * width / prepWidth.D, 0, width);
                                            var bottom = Math.Clamp(rows2[b] * height / prepHeight.D, 0, height);
                                            if (right <= left || bottom <= top || !classIds.Contains(classes[b]) ||
                                                !double.IsFinite(scores[b]) || scores[b] < 0 || scores[b] > 1)
                                                throw new InvalidDataException("检测结果存在非法框、类别或置信度。");
                                            boxes.Add(new { classId = classes[b], confidence = scores[b],
                                                col1 = left, row1 = top, col2 = right, row2 = bottom });
                                        }
                                        results.Add(new { imagePath = path, width, height, boxes });
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is HalconException or IOException or InvalidDataException)
                            {
                                errors.Add(new { imagePath = path, error = ex.Message });
                            }
                            report(new { stage = "预测", processed = i + 1, total = imagePaths.Length, successful = results.Count, failed = errors.Count });
                        }
                        if (results.Count == 0)
                            throw new InvalidOperationException("批量预测全部失败；请检查模型、输入图像和运行环境。");
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputJson))!);
                        var id = Path.GetFileNameWithoutExtension(modelFile) + ":" +
                            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelFile)))[..12];
                        File.WriteAllText(outputJson, JsonSerializer.Serialize(new
                        {
                            schemaVersion = 1, modelId = id,
                            classes = classIds.Select((classId, index) => new { id = classId, name = classNames[index] }),
                            images = results
                        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                        File.WriteAllText(Path.ChangeExtension(outputJson, ".errors.json"),
                            JsonSerializer.Serialize(errors, new JsonSerializerOptions { WriteIndented = true }));
                        return new { processed = imagePaths.Length, successful = results.Count, failed = errors.Count, outputJson };
                    }
                }
            }
        }
    }

    private static double[] ReadDoubles(HTuple dictionary, string key)
    {
        HOperatorSet.GetDictTuple(dictionary, key, out var values);
        using (values) return values.ToDArr();
    }
    private static int[] ReadInts(HTuple dictionary, string key)
    {
        HOperatorSet.GetDictTuple(dictionary, key, out var values);
        using (values) return values.ToIArr();
    }
}
