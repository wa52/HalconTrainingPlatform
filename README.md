# HALCON 训练平台

独立 Windows x64 / .NET 8 **WPF 桌面程序**。当前正式入口是 `src/HalconTraining.App/bin/Release/net8.0-windows/HalconTraining.App.exe`，桌面“HALCON 训练平台”快捷方式直接指向它。运行不需要浏览器、Node、HTTP或WebView。

## 当前功能

- 三页工作台：数据集、训练、模型；任务与环境集中在顶部，低频参数、日志和详细指标默认折叠。
- 直接浏览原始 `.hdict/.json`，任务名使用数据集文件名；任务识别、固定种子和工件/批次分组划分；源文件不改写。
- 根据任务匹配项目 `assets/pretrained` 中的模型副本。常显可编辑参数：图像尺寸、Batch、轮数、学习率；比例、集合数量和基础模型紧凑展示；高级项：种子、划分比例调整和验证间隔。训练开始后自动收起参数、扩大监控区域；完成后可直接查看模型并测试。
- 图像分类与语义分割真实训练、实际Loss/日志、独立评估、停止Worker、原子状态文件与中断识别。停止保留产物，不支持断点续训。
- 模型固定保存到 `models/<任务类别>/<数据集名>/<运行编号>`；包含模型、预处理、类别、参数、划分、评估、日志和SHA-256。只有独立评估完成的分类或语义分割模型可测试。
- 原图批量测试与坏图隔离，`HSmartWindowControlWPF`原生彩色显示、青色预测叠加、透明度调节和原尺寸PNG保存。测试结果保存在 `results/tests`，重启后按模型恢复历史。
- 工作区单实例、关闭时回收Worker。运行环境通过HALCONROOT/HALCONARCH和许可证配置；可用HALCON_TRAINING_HOME指定工作区。

图像分类和语义分割已完成真实训练/测试流程验收。分类监控显示验证准确率，原图测试显示预测类别和全类别置信度；独立报告包含混淆矩阵和每类召回率。目标检测已接入下述文件夹标注、全部未移除图像合并训练和原图预标注流程，尚未完成独立 mAP 验收；实例分割、OCR和异常任务可读取/识别及匹配基础模型，尚未接通完整训练适配器。

## 目标检测：全部图像训练

数据集页的文件夹导入按子文件夹组织类别，原图就地读取。训练包含全部未移除图像，不再根据“已确认”状态筛选：有人工草稿/标注时使用当前人工框，否则使用已有预测框；当前没有框的图像自动作为负样本，不需要单独标记，也不自动补框。人工清空的草稿不会恢复旧预测框。至少需要 5 张图像并有目标框；全为负样本的文件夹应与有框类别合并训练。

顶部“导出 .hdict”和“合并 HALCON 训练”使用相同全量快照。背景文件夹的图片参与训练，但不会凭目录名创建没有目标实例的检测类别。HALCON 数据集仍分为训练/验证/测试，优化器只读取训练划分。确认、全部确认和仅已确认 COCO 导出保留为复核功能，不是训练准入条件。详见 [目标检测流程](docs/ACTIVE_LEARNING_DETECTION.md)。

单类别训练保持文件夹隔离；多个有框类别可按 1 或 2 个并发运行，数量门槛统计所有图片。模型保存到 `models/detection/<类别或数据集名>/run_.../`；最终合并训练生成一个多类别模型。当前编辑的框在训练/HDICT 导出前保存为草稿，预测框不会因此被标成“人工已确认”。导出报告记录全部图片数、负样本数及标签来源。

图像列表标题旁的图片减号图标仅从会话移除图像及标注，磁盘原图保留。移除后刷新列表、缩略图与数量；重启、重新扫描及预测合并不会自动加回。操作前会话备份在 `active-learning/sessions/backups`。已有模型和历史导出不改写；源 `.hdict` 浏览仍只读。

本机已完成 HALCON/GPU 真实训练和原图预测冒烟；本轮全量/负样本改动完成数据层、COCO→YOLO 转换、WPF 和真实 HALCON HDICT 写入复读检查。本轮未重新执行 GPU 训练、完整 mAP 或 HALCON 并发 GPU 验收，不能据此声称精度达标。

## 架构

- Domain：任务和状态、统一数据划分，不依赖WPF/HALCON。
- Application：工作流、DTO、Worker和存储接口。
- Persistence：完整模型包、原子作业状态、模型索引、桌面会话和测试历史。
- Halcon：字典识别、训练、评估、预测与叠加图生成。
- Worker：单任务短生命周期进程；GPU和模型句柄不进入界面。
- App：原生WPF + MVVM、系统文件选择器、独立进程客户端、HSmartWindow显示适配器。

## 构建与运行

要求Windows x64、.NET 8、HALCON24.11和本机许可、GPU运行环境。

首次克隆后，需将已授权 HALCON 安装目录 `HALCONROOT/dl` 中与 `assets/pretrained/catalog.json` 对应的基础模型复制到 `assets/pretrained`。权重文件不随源码上传；分类和语义分割分别需要 `pretrained_dl_classifier_compact.hdl` 与 `pretrained_dl_segmentation_compact.hdl`。配置好 `HALCONROOT`、`HALCONARCH` 和本机许可证后再运行。

```powershell
dotnet build .\src\HalconTraining.App\HalconTraining.App.csproj -c Release
.\src\HalconTraining.App\bin\Release\net8.0-windows\HalconTraining.App.exe
```

离线缓存包构建可加 `--no-restore -p:NuGetAudit=false`，这不是依赖安全审计结果。界面关闭时会停止当前Worker并记录中断。

## 验证

WPF程序支持显式开发验证参数 `--validate-desktop --validation-output <证据目录> --dataset <字典> --image <原图1> --image <原图2>`，只有指定此参数才执行一轮真实训练、评估、批量预测、原生显示、停止和重载检查。建议通过进程级HALCON_TRAINING_HOME设置独立验证工作区，复制所需基础模型和已验证模型到其中，避免将验证作业放入正式模型库。

三页交互与真实分类/分割回归证据在 `artifacts/ui-simplicity`，早期证据在 `artifacts/wpf-validation`（真实数据/模型/截图不提交）。网页时期的HTML与Node脚本仅保留为历史设计和验证资料，不属于桌面运行依赖。

设计与流程见[WPF_IMPLEMENTATION.md](docs/WPF_IMPLEMENTATION.md)。训练产物和真实图像均不随源代码提交。
分类流程和数据生命周期见 [docs/CLASSIFICATION.md](docs/CLASSIFICATION.md)。

UI 的最高标准为“简洁、有效、清晰，以操作效率为第一目标”，具体准则见 [UI_SIMPLICITY.md](docs/UI_SIMPLICITY.md)。

操作使用共用矢量图标；三页导航、主要训练/确认/测试操作及折叠分区保留必要中文标题，文件管理与次级工具使用图标。中文提示每次悬停 1.5 秒后出现，移动到另一图标时重新等待。参数和结果保留文字。重复的划分更新、合并导出与导航按钮已合并，长内容可滚动或悬停查看；支持 Ctrl+1/2/3 切换页面、Ctrl+O/Ctrl+Shift+O 选择数据和 Ctrl+S 按当前页面保存。空数据集、空模型测试与空闲监控提供紧凑的下一步指引，有数据后恢复图像和实际监控区域。

通用设计标准见 [UI_UX_STANDARD.md](docs/UI_UX_STANDARD.md)，设计分析、自评与真实验证范围见 [UI_VISUAL_REVIEW.md](docs/UI_VISUAL_REVIEW.md)。图标、可访问名称、最小/常用尺寸、参数错误反馈、显示状态切换、分栏、长类别和模式切换可通过独立工作区下的 `--validate-ui-policy --validation-output <目录>` 检查；该验证不启动训练，运行状态属于显示层测试条件，不是新的 GPU 训练证据。仅显式 `--review-ui` 审查模式将窗口置前；普通启动不改变窗口置顶行为。

顶部「导出 .hdict」合并全部未移除图像；「合并 HALCON 训练」导出后训练一个多类别模型。有框图像使用当前框，无框即负样本，确认状态不再筛选训练数据。详情见 [目标检测流程](docs/ACTIVE_LEARNING_DETECTION.md)。

顶部右侧常显实时 GPU 利用率、温度和显存占用，每两秒通过 `nvidia-smi` 采样。悬停可查看显卡型号、功耗及全部 GPU 信息；显示的是包含其他程序在内的整卡占用。超时、工具缺失或采样失败显示不可用并自动重试，不显示为 0%，也不替代 HALCON 授权/兼容性检查。窗口关闭后停止采样并清理查询进程。

GPU 采样、未知值、超时/取消与轮询生命周期回归：`dotnet run --project tests/HalconTraining.GpuTelemetry.Smoke/HalconTraining.GpuTelemetry.Smoke.csproj -c Release -p:NuGetAudit=false`。显式原生界面验证入口为 `--validate-gpu-status --validation-output <证据目录>`，请使用独立的 `HALCON_TRAINING_HOME`。

HALCON 目标检测现支持兼容模型继续微调、手选中断检查点、继承配套预处理、逐批吞吐量与阶段耗时、每轮原子检查点。Epoch 为本轮追加轮数，不是精确恢复优化器状态。兼容性、界面、数据回归和两轮真实 GPU 续训已通过；等精度耗时对比与独立 mAP 仍待验证。实测数据准备仅占约 1.4%，层融合无明显收益，因此未默认开启缓存或融合。详见 [训练性能与模型复用](docs/TRAINING_PERFORMANCE.md)。
