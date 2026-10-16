# 目标检测主动学习 v1（rectangle1）

本模块位于原生 WPF **数据集 → 目标检测 · 主动学习**，不改变已有分类、语义分割训练链。

## 功能边界

- 从检测模型的**批量预测 JSON**导入原图路径、水平检测框、类别、置信度，可选第二类别得分和工件/批次号。
- 用每图最低框置信度、最接近的前两类别得分差排序；无框图片也进入复核清单（可能漏检，也可能是真正无目标），并非自动判断漏检。
- 原生 HALCON 图像视口显示原图和检测框；选择图片后可在表格编辑类别ID、左上/右下坐标，添加或删除框。
- 支持**保存草稿 / 接受并下一张 / 需重标 / 跳过**；审核会话保存于 `active-learning/sessions`，启动后自动恢复最近一次。
- 只有状态为 **已确认（approved）** 的图片与人工编辑过的框可以导出。需重标/待审核图片严格排除。**已确认且无框**的图片导出为已审核的负样本。
- 导出 COCO `annotations.json`、配套 `images/` 原图副本以及 `provenance.json`（模型来源、审核会话、未审核数量），目录 `active-learning/exports/<时间_编号>/`。原数据集和预测 JSON 只读。
- 仅支持 **rectangle1** 水平框，不支持旋转框、实例掩膜。本版并不把模型预测作为真值，也未接通目标检测训练/自动推理；需要用户提供已有模型导出的预测 JSON。

## 预测输入 JSON（schemaVersion 1）

以下示例路径相对 JSON 文件所在目录；对应原图文件必须存在。

```json
{
  "schemaVersion": 1,
  "modelId": "detector-v1",
  "classes": [
    {"id": 1, "name": "scratch"},
    {"id": 2, "name": "dent"}
  ],
  "images": [
    {
      "imagePath": "images/001.png",
      "width": 1920,
      "height": 1080,
      "groupId": "batch-001",
      "boxes": [
        {
          "classId": 1,
          "confidence": 0.52,
          "runnerUpConfidence": 0.45,
          "col1": 310, "row1": 210,
          "col2": 480, "row2": 290
        }
      ]
    },
    {
      "imagePath": "images/002.png",
      "width": 1920,
      "height": 1080,
      "groupId": "batch-002",
      "boxes": []
    }
  ]
}
```

坐标全部使用**原图像素**：`(col1,row1)` 是左上角，`(col2,row2)` 是右下角（非 YOLO 归一化中心点坐标）。
`confidence` 取 0–1，`runnerUpConfidence` 可省略但若提供必须不大于第一得分。
`classId` 必须匹配 `classes[].id`；所有框严格约束在图像尺寸以内。
`groupId` 仅记录来源，**本版尚未实现分组多样性采样**。导入时检查重复路径、源图片存在性以及字段有效性。
类别和框可在审核后修改，**仅接受操作才会把图片变成可导出的已验证标签**。
编辑框表中的“得分”始终指**原模型得分**，并不因人工改框而变成新的模型置信度。人工新增框记为 0。

## 主动学习迭代闭环

1. 从外部检测模型导出预测 JSON；导入到数据集的主动学习子页。
2. 优先核对低置信度、近似同分和无检测框图片。所有图片均须人工检查，分数只表示审核优先级，不是正确率。
3. 修正框、类别、漏检目标，点击“接受并下一张”。有问题标记“需重标”，不要当成无目标。
4. 导出已确认 COCO；保留训练集和锁定测试集，不把同工件、同批次、近重复图片泄漏到训练/验证/测试之间。
5. 后续通过 HALCON `read_dl_dataset_from_coco` 读取经过人工核验、拆分后的 COCO 检测数据，并完成独立目标检测训练与评估。训练新模型后重复此循环。

**警告**：COCO 导出不是已经完成的 HALCON 目标检测训练入口。是否能直接传入 HALCON，还必须按 HALCON 24.11 的 COCO 数据导入过程、图片路径与类别映射检查，并对实际数据执行最小训练验收。外部模型预测导入是第一阶段临时入口，目标检测推理适配器尚未实现。

## 无 HALCON 的数据层冒烟测试

在具备 .NET 8 SDK 的环境运行：

```powershell
dotnet run --project tests/HalconTraining.ActiveLearning.Smoke/HalconTraining.ActiveLearning.Smoke.csproj
```

测试包括：相对路径预测导入、类别竞争不确定性排序、无框图片保留、已确认标注恢复、人工修改框和COCO xywh 输出、拒绝越界框。该测试不需要 HALCON、GPU 或许可证。WPF 渲染和真实目标检测推理仍需 Windows + HALCON 现场验收。

## 第二阶段

- HALCON 目标检测 `.hdl` 原图批量推理 → 自动生成以上预测 JSON，消除外部 JSON 转换。
- 支持在图上拖拽绘制、移动和缩放检测框；优先级混合分组/批次多样性、类别配额与近重复图剔除。
- HALCON 目标检测训练、独立 mAP/PR/漏检检查以及模型版本对比；只把人工审核集加入下一轮训练。
