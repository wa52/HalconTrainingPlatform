# 目标检测主动学习 v1（rectangle1）

本模块位于原生 WPF **数据集 → 目标检测 · 主动学习**，不改变已有分类、语义分割训练链。

## 从数据集移除图像（保留原图）

选中图像列表中的一张图片，点击列表标题旁的图片减号图标。大图和缩略图模式共用这一个入口，与“删除所选框”分开。悬停 1.5 秒显示中文说明，无选择或忙碌时禁用。确认窗口展示完整路径和影响，默认取消；移除后自动选择相邻图像，最后一张移除后清空画布和标注。

仅修改当前标注会话：原图不删除，其他类别的标注、类别 ID、已有模型和历史导出保持不变。图像与其人工/预测标注从会话列表移除，因此新预测候选、新 COCO/HDICT 导出以及下一次合并训练均不包含它。移除记录在重启、重新扫描、追加原图和预测合并后仍有效；不会因为磁盘文件还存在就自动恢复。

保存使用原子替换；发布新会话前先保存成功。操作前会话快照保存在 `active-learning/sessions/backups`，不混入当前会话自动加载列表。写入失败时保留原会话和选择，不修改源文件。当前不提供界面恢复按钮，若需撤销可从会话备份恢复；源 `.hdict` 浏览仍只读。

验证：数据层冒烟覆盖持久化、重新扫描、预测合并、导出/训练排除、其他类别、最后一张和写入失败；显式原生验证参数 `--validate-image-removal --validation-output <目录>` 要求 `HALCON_TRAINING_HOME=<目录>/workspace`，只对其自行创建的隔离图片执行检查，不训练。追加 `--manual-image-removal-check` 保留确认窗口供真实审查，Esc 取消、Alt+F4 退出。

## 文件夹模式：每个类别独立训练和预测

点击「导入文件夹」，选择含有类别子目录的根目录。例如「结果/NGP1」「结果/P1」会建立两个独立类别工作区。

- 自动读取目录名和原图，不复制原图、不自动生成检测框。子目录内更深层的批次目录仍属于该类别；根目录中没有类别归属的图片会跳过并提示。
- 左侧只显示当前类别的图像，画框默认使用该目录类别。挑几张有代表性的图像画框，点击「确认并下一张」。
- **每个类别单独至少确认5张，且有有效目标框**。不能把多个文件夹的数量凑成5张；训练快照只含当前目录的一个类别和已确认图像。
- 点击「开始训练」后，模型分别保存到 `models/detection/NGP1/run_.../`、`models/detection/P1/run_.../` 等固定类别目录。切换类别会切换到其自己的模型。
- 点击「预测其余图像」直接预测当前类别内未确认、无人工草稿的图像，无需重新选图。预测结果待审核；已确认框、草稿、失败图像及其他类别的数据均保留。
- 重复选择同一根目录会补充新图，保留原有类别ID、确认结果及各类别模型；坏图单独跳过，详细原因在「更多操作」中查看。
- 四项常用参数直接可编辑，运行时锁定。坐标、单图添加、已有模型、导出和读取提示放在折叠的低频区域。

目录模式按原路径读取图片。审核会话仍存于本机 `active-learning/sessions/`，不会写回原图目录。

## 标注完成：合并导出 HALCON 数据集并训练

顶部「导出 .hdict」把所有类别的 **approved** 图像合成一份多类别 DLDataset。当前未审核、仅有预测框、草稿和需重标图像全部排除；没有已确认图像的文件夹类别不加入。类别 ID/名称与人工确认框保持原值，不根据目录名生成框。已确认的空框图像作为负样本保留。

数据流：审核会话快照 → 已确认图像与 COCO 快照 → HALCON `create_dict/set_dict_tuple/write_dict` → 原生二进制 `.hdict` → `read_dict` 逐项复读校验 → HALCON 训练。COCO 额外保留 `halcon_bbox` 原始行列坐标，避免 xywh 往返的小数误差。

每次导出到 `active-learning/exports/<时间_唯一编号>/`，包含 `<数据集名>.hdict`、`images/` 原图副本、`annotations.json`、`provenance.json` 和导出报告。`image_dir=images`，整个目录可以一起移动；不要只复制字典而遗漏原图。旧导出文件不会被覆盖。

按照标注类别分层、固定种子 42 自动划分训练/验证/测试（70%/15%/15%）。极少样本可能出现空集合，导出报告保留实际数量；图像级划分不能证明相似工件之间无泄漏。训练优化器只使用 `split=train`，验证和测试图片不会参与权重更新；本流程尚未执行独立 mAP 验收。

点击「合并 HALCON 训练」直接使用显示的图像尺寸、Batch、Epoch、学习率，先导出上述数据集，再训练一个多类别 `.hdl` 模型，放在 `models/detection/<根数据集名>/run_.../`。正式合并模型以 `CombinedModelPath` 登记，不覆盖各文件夹的预标注模型、人工标签或原始图像。「本类预标注训练」保留原有单类别预标注流程。

HALCON 24.11 检测模型的输入尺寸是 `create_dl_model_detection` 的创建参数，不能沿用分类模型的创建后 `set_dl_model_param(image_width/image_height)`。训练通过 Worker 隔离，保存 `preprocess.hdict`、`model.hdl`、训练日志、类别映射和实际划分数量。

原生数据集回归（需要 Windows HALCON 运行时）：

```powershell
dotnet run --project tests/HalconTraining.DetectionDataset.Smoke/HalconTraining.DetectionDataset.Smoke.csproj -c Release -p:NuGetAudit=false
```

覆盖真实二进制读写、人工确认筛选、空框负样本、小数坐标、类别映射、多类别分层划分、目录迁移和非法框拒绝。显式实机批量启动参数 `--train-reviewed-hdict` 会对当前审核会话的已确认图像合并执行 HALCON 训练（5 Epoch、Batch 2、256 像素、学习率 0.0001），在 WPF 显示过程并将证据写入 `artifacts/hdict-training/native-batch/`；普通启动不自动训练。

## 启动数量：最少5张，标多少由用户决定

- 当前类别内**至少5张人工确认的图片**才允许微调预训练检测模型；20、50或100张只是参考，不是限制。
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

**限制和风险**：首轮少量图像只用于形成预标注候选，不等于拥有可靠工业检测能力。应保证多场景和多批次代表性，并为正式评估单独留出训练之外的验证/测试集，不能以训练 Loss 当作精度。此版还没有按工件元信息的防泄漏划分、独立 mAP 验收、HALCON 持续训练旧模型、旋转框和实例分割。选择HALCON引擎时仍依赖本机 HALCON 深度学习授权、可兼容的 GPU 和合法来源的 `pretrained_dl_classifier_compact.hdl`；HALCON 24.11 对特定新 GPU（如 RTX 5060）不能仅凭显卡存在就认定支持，必须在现场验证训练算子。

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

新增目录回归包括：目录类别识别、相同文件名去重依据完整路径、坏图隔离、重复读取、草稿保护、取消、每目录训练快照及模型隔离、拒绝跨目录预测。Windows 可显式使用 `--detection-folder <根目录> --validate-folder-detection --validation-output <证据目录>` 验证真实WPF布局；应设置进程级 `HALCON_TRAINING_HOME` 到隔离目录。该检查不训练，不确认其绘框测试，不会把测试框写成训练标签。

## 训练与预测的实时反馈

「并行训练…」可选择至少两个已分别确认五张以上的类别，使用当前显示的 Epoch、Batch、图像尺寸和学习率启动各类别独立训练。可选同时运行数 1 或 2，超过名额的类别排队。每个任务拥有独立 COCO 快照、Worker 进程、进度/Loss、日志和模型目录；模型登记串行合并，避免后完成任务覆盖其他类别的模型映射。失败不取消其他类别；「停止」取消运行中任务及排队任务，已完成模型保留。

运行时参数和审核编辑保持锁定，主工作台训练与该队列仍互斥。单张 GPU 的任务共享显存，并行不保证提速；显存不足会明确显示该类别失败，可减小 Batch/尺寸或将同时运行数设为 1。首次使用需准备可用预训练权重。真实并行验收以两个独立 YOLO Worker 的 PID 与运行时间重叠、各自完成轮次和单类别模型包为证据；HALCON 引擎的许可证/GPU 并发能力仍需分别实机验收。

预测阈值直接显示在图像区上方，用 0～1 的小数编辑，默认 0.05（5%）；文件夹模式按类别分别保存。YOLO Worker 和 HALCON 模型的 `min_confidence` 均接收本轮阈值。完成提示和 `prediction-summary.json` 分别记录已处理、有框、未检出、总框数和失败数；全部为空时明确提示「未检出目标」，不能将处理成功当成检测成功。列表、缩略图和大图详情同步显示空检测与框数。低分候选只供人工审核，降低阈值不等于提高模型质量。

「全部确认」只作用于当前类别内有有效检测框的待审核图片，按当前每图预测框数量取候选，保留已有人工草稿。点击时列出确认范围、数量和低分候选数量；无框、坏框、原图缺失、需重标以及其他类别均跳过。取消不会改变标注。确认后先保存 `active-learning/review-backups/` 中的完整操作前快照，再原子更新审核会话；下一轮训练只读取确认后的标签。

中间图像区提供「大图编辑 / 缩略图总览」切换。总览按当前类别显示全部图片，可滚动浏览，缩略图叠加与大图一致的已保存标注或预测框，显示文件名、审核状态和框数；点选即打开该图大图编辑。其他类别不会混入。缩略图限制解码宽度为 128 像素，读完立即释放原图文件；无图时保留可定位的条目，不因此退出程序。

文件夹模式默认每张图最多一个预测框，按模型得分降序保留；「更多操作 → 每图最多预测框」可改为 1～300。该限制传入 YOLO Worker，并应用于 HALCON 结果进入审核队列后的筛选。零检测仍为零框，不强行生成框。历史未审核结果在显示时使用相同筛选；原始候选记录、人工草稿和已确认标注保持完整。修改数量后重新选择图片或重新预测生效，执行预测时随会话和本轮 `prediction-options.json` 保存。单框限制是已知目标数量的业务约束，不能把低得分候选变成可靠检测。

点击训练后立即显示准备阶段并锁定参数，随后显示实际 Worker 的初始化、Epoch/总轮数、Loss 和耗时；预测显示已处理/总图片数。完成、停止和失败均保留明确结果，详细输出在默认折叠的「运行日志」中查看。每轮训练和预测目录分别保存 `training.log`、`prediction.log`。

Python Worker 管道统一使用 UTF-8，关闭输出缓冲，避免 Windows GBK 环境导致中文阶段无法识别、实际已执行但界面没有更新。进度完成代表本轮作业成功，不代表检测精度通过验收。

编码回归随数据层冒烟测试执行；真实 WPF/GPU 反馈可在独立 `HALCON_TRAINING_HOME` 中使用 `--validate-detection-feedback --validation-output <证据目录>` 验证。它需要已有至少五张人工确认图片的审核会话，会执行两轮训练和两张原图预测，仅用于显式验收，不能指向用户的正常工作目录。

## 下一阶段

- 批次/工件防泄漏的数据划分、独立验证集与 mAP/召回率评估，形成正式已验收检测模型。
- 真实 HALCON 24.11 Windows + GPU 编译、训练与批量推理逐项验收；提高绘框时的缩放/移动手感。
- 增量训练（从上一轮权重继续）和最佳模型管理，添加样本多样性/近重复特征筛选。
