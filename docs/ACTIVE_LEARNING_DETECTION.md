# 目标检测主动学习 v1（rectangle1）

本模块位于原生 WPF **数据集 → 目标检测 · 主动学习**，不改变已有分类、语义分割训练链。

## 启动数量：最少5张，标多少由用户决定

- **至少5张人工确认的图片**就允许微调预训练检测模型；20、50或100张只是参考，不是限制。
- 每个类别至少有1个已审核的目标框。确认无目标的图片可以作为负样本。
- 可以随时点击「继续添加图片」累积标注，不会覆盖已接受的标签；界面提示距离5张还差多少。
- 5张训练的模型只用于下一轮预标注，**不是生产可用或已通过mAP验收的检测模型**。
- 默认使用YOLO26n检测预训练权重微调；可以切换至HALCON目标检测实验引擎。

## YOLO Worker 环境和模型选择

YOLO微调与推理运行于独立Python进程，脚本位于 `tools/yolo_active_learning.py`，WPF打包后位于 `worker/yolo_active_learning.py`。默认尝试系统Python，也可以设置环境变量 `HALCON_TRAINING_PYTHON` 指向独立虚拟环境中的python.exe。

Windows PowerShell示例：

```powershell
python -m venv .venv-yolo
.\.venv-yolo\Scripts\python.exe -m pip install -U pip
.\.venv-yolo\Scripts\python.exe -m pip install ultralytics
$env:HALCON_TRAINING_PYTHON = (Resolve-Path .\.venv-yolo\Scripts\python.exe).Path
```

首次使用 `yolo26n.pt` 可能需要网络下载权重。PyTorch GPU/CUDA版本需与你的显卡兼容；无CUDA可回退CPU（但会很慢）。商业闭源发布请核查Ultralytics授权条款。

默认核心参数：40 Epoch、Batch 2、640像素、学习率0.001（均可编辑）。训练使用全部已批准样本，不从极少量种子里伪造独立评估集，`val=False`；不能把训练Loss理解为泛化精度。

「已有模型批量预测」支持YOLO `.pt` 和HALCON `.hdl`。HALCON模型必须在同目录提供与训练匹配的 `preprocess.hdict`；YOLO模型直接由PyTorch Worker加载。

不依赖YOLO/GPU的数据层自动测试：`python -m unittest discover -s tests -p 'test_yolo_active_learning.py' -v`。
## 两条入口、同一个审核队列

**入口 A：从零启动**
1. 点击「选择图片先标注」，多选几张代表性图片，输入类别名（用逗号分隔）。不要求已有模型。
2. 在图像视口按住鼠标左键拖动生成矩形框，或点击「添加框」并修改坐标。选择类别 ID，逐张点击「接受并下一张」；确认没有目标的图片可以接受为负样本。
3. 至少确认 **5 张图片**、每个类别至少一个真实目标框后，核对 Epoch / Batch / 尺寸 / 学习率，点击「开始微调训练」。
4. 训练结果写入 `models/detection/<会话ID>/<运行ID>/`；YOLO保存 `model.pt`，HALCON保存 `model.hdl` 和 `preprocess.hdict`，并记录 `manifest.json`。状态标记为 `bootstrap_unvalidated`：它可以参与主动学习预标注，**没有独立 mAP、召回率或生产精度验收**。
5. 点击「使用本轮模型预测」并多选新图片；Worker 批量推理后自动进入审核队列，继续修改、接受、导出、训练。

**入口 B：已有模型**
1. 点击「已有模型批量预测」，选择已训练的 YOLO `.pt` 或 HALCON `.hdl` **rectangle1 检测模型**（不是分类/语义分割模型）。
2. `.hdl` 同目录必须提供与训练匹配的 `preprocess.hdict`，否则拒绝推理，避免错误归一化或坐标映射。
3. 多选原图，完成后自动生成预测 JSON 并进入审核队列，无需人工编辑 JSON。模型类别从 `.hdl` 读取，不支持混入不同类别定义的历史审核标签。

原先「导入预测JSON」仍然保留，便于外部模型生成预测结果。图片、标注源数据只读；只导出人工确认的图像和检测框（含已确认负样本）。审核可保存草稿、退回重标和重启恢复。
选择同类别定义的模型再预测时，既有 approved 图片不被新预测覆盖；任务和类别不兼容则新建独立审核队列、不合并旧标签。

**限制和风险**：首轮少量图像只用于形成预标注候选，不等于拥有可靠工业检测能力。应保证多场景和多批次代表性，并为正式评估单独留出训练之外的验证/测试集，不能以训练 Loss 当作精度。此版还没有自动划分组内防泄漏、独立 mAP 验收、持续训练旧模型、旋转框和实例分割。选择HALCON引擎时仍依赖本机 HALCON 深度学习授权、可兼容的 GPU 和合法来源的 `pretrained_dl_classifier_compact.hdl`；HALCON 24.11 对特定新 GPU（如 RTX 5060）不能仅凭显卡存在就认定支持，必须在现场验证训练算子。

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
`groupId` 用于审核队列按工件/批次轮换，降低同组样本连续占据前排的情况；未提供时视为独立图片。**尚未实现近重复图特征检索或类别配额**。导入时检查重复路径、源图片存在性以及字段有效性。
类别和框可在审核后修改，**仅接受操作才会把图片变成可导出的已验证标签**。
编辑框表中的“得分”始终指**原模型得分**，并不因人工改框而变成新的模型置信度。人工新增框记为 0。

## 数据层与训练闭环

- 模型检测原生 Worker 读取 `bbox_class_id / bbox_confidence / bbox_row1 / bbox_col1 / bbox_row2 / bbox_col2`，检查模型类型、rectangle1、full_domain 预处理和 GPU；将预测框按原始图尺寸映射，生成与外部 JSON 输入一致的审核清单。
- 低置信度、类别得分接近、无检测框图片优先进入人工审核。无框不自动等于漏检，必须由人确认。
- 同工件/批次图片按组轮换审核。合并老审核记录时仅保留类别定义相同且人工确认的标签；不自动批准模型预测。
- 训练以人工确认导出的 COCO 为唯一标签来源。使用 HALCON 24.11 `create_dl_model_detection` 及 `train_dl_model_batch`，提供轻量 **bootstrap**，先生成能用于下一轮预标注的模型，不伪装为可部署的已验证版本。
- 推理结果位于 `active-learning/predictions/`；审阅状态位于 `active-learning/sessions/`；人工确认的导出快照位于 `active-learning/exports/`，原始图片不改写。
- 工作台与主动学习的 GPU 任务互斥；窗口关闭时取消正在运行的 Worker，取消/失败作业不被登记为可验收模型。

**尚待生产验收**：HALCON 目标检测正式数据划分、独立评估 mAP/PR、最佳模型选择、增量轮次训练、训练/部署模型兼容性以及 GPU 实机冒烟测试。默认目标检测训练设置仅为首轮探索的起点，不是推荐的最终精度参数。

## 无 HALCON 的数据层冒烟测试

在具备 .NET 8 SDK 的环境运行：

```powershell
dotnet run --project tests/HalconTraining.ActiveLearning.Smoke/HalconTraining.ActiveLearning.Smoke.csproj
```

测试包括：5张启动门槛、追加图片时保留已审核标签、从零创建人工标注会话、未审核禁止导出、相对路径预测导入、类别竞争不确定性排序、分组轮换、无框图片保留、审核恢复、人工修改框和COCO xywh 输出、拒绝越界框。该测试不需要 HALCON、GPU 或许可证。WPF 渲染和真实目标检测推理仍需 Windows + HALCON 现场验收。

## 下一阶段

- 批次/工件防泄漏的数据划分、独立验证集与 mAP/召回率评估，形成正式已验收检测模型。
- 真实 HALCON 24.11 Windows + GPU 编译、训练与批量推理逐项验收；提高绘框时的缩放/移动手感。
- 增量训练（从上一轮权重继续）和最佳模型管理，添加样本多样性/近重复特征筛选。
