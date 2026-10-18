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

真实训练/测试已验收的任务为**图像分类和语义分割**。分类监控显示验证准确率，原图测试显示预测类别和全类别置信度；独立报告包含混淆矩阵和每类召回率。目标检测、实例分割、OCR和异常任务可读取/识别及匹配基础模型，尚未接通完整训练适配器，界面不冒充可执行。

## 目标检测主动学习（审核/数据准备）

支持按文件夹选择多个类别进行并行训练，同时运行数可设为 1 或 2，其余排队。各类别分别显示 Epoch、Loss、耗时并保存独立模型；停止会取消运行与排队任务。P2/P3 两个 YOLO Worker 的真实重叠运行、独立模型登记及三任务取消已通过本机验证；HALCON 引擎并发与正式检测精度仍需独立验收。

主要入口为「导入文件夹」：根目录的每个子文件夹是一个**独立类别**，分别挑图画框、确认、训练和预测。当前类别至少确认5张后才能训练；模型分别保存在 `models/detection/<类别名>/run_.../`。点击「预测其余图像」仅处理该类别，保留人工标注和草稿，不跨文件夹混合训练或共享模型。

数据集页新增「目标检测 · 主动学习」：**无模型时**标注任意数量图片，**人工确认满5张且每类别有有效检测框**即可使用YOLO26n预训练权重进行探索性微调；训练得到未验收检测模型，可直接对新图片批量预标注。**已有检测模型时**选择YOLO `.pt` 或带有匹配 `preprocess.hdict` 的HALCON `.hdl` 并多选图片，直接批量预测，无需手写 JSON。审核队列按置信度、类别得分差及工件/批次排序，支持修改/添加/删除框、恢复审核会话，只将人工确认标签导出为 COCO。详见 [docs/ACTIVE_LEARNING_DETECTION.md](docs/ACTIVE_LEARNING_DETECTION.md)。

**边界**：已接入YOLO独立Python训练/预测Worker及HALCON检测Worker，**仅提供探索性bootstrap**，未通过 Windows HALCON+GPU 实机验收、独立 mAP 与正式模型质量验收。不能把它视为完整的生产级目标检测训练适配器；原本的分类/语义分割独立验收与模型发布流程仍保持不变。可用 `dotnet run --project tests/HalconTraining.ActiveLearning.Smoke` 运行无 HALCON 的数据层验证。

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
dotnet build .\HalconTrainingPlatform.sln -c Release
.\src\HalconTraining.App\bin\Release\net8.0-windows\HalconTraining.App.exe
```

离线缓存包构建可加 `--no-restore -p:NuGetAudit=false`，这不是依赖安全审计结果。界面关闭时会停止当前Worker并记录中断。

## 验证

WPF程序支持显式开发验证参数 `--validate-desktop --validation-output <证据目录> --dataset <字典> --image <原图1> --image <原图2>`，只有指定此参数才执行一轮真实训练、评估、批量预测、原生显示、停止和重载检查。建议通过进程级HALCON_TRAINING_HOME设置独立验证工作区，复制所需基础模型和已验证模型到其中，避免将验证作业放入正式模型库。

三页交互与真实分类/分割回归证据在 `artifacts/ui-simplicity`，早期证据在 `artifacts/wpf-validation`（真实数据/模型/截图不提交）。网页时期的HTML与Node脚本仅保留为历史设计和验证资料，不属于桌面运行依赖。

设计与流程见[WPF_IMPLEMENTATION.md](docs/WPF_IMPLEMENTATION.md)。训练产物和真实图像均不随源代码提交。
分类流程和数据生命周期见 [docs/CLASSIFICATION.md](docs/CLASSIFICATION.md)。

UI 的最高标准为“简洁、有效、清晰，以操作效率为第一目标”，具体准则见 [UI_SIMPLICITY.md](docs/UI_SIMPLICITY.md)。

目标检测标注完成后，顶部「导出 .hdict」合并所有类别已确认图像；「合并 HALCON 训练」导出后训练一个多类别模型。未确认、仅预测、草稿及需重标图像不会进入训练。详情见 [目标检测流程](docs/ACTIVE_LEARNING_DETECTION.md)。
