# HALCON 训练平台

独立 Windows x64 / .NET 8 **WPF 桌面程序**。当前正式入口是 `src/HalconTraining.App/bin/Release/net8.0-windows/HalconTraining.App.exe`，桌面“HALCON 训练平台”快捷方式直接指向它。运行不需要浏览器、Node、HTTP或WebView。

## 当前功能

- 四页工作台：项目总览、数据集、训练、模型管理；低频参数折叠。
- 直接浏览原始 `.hdict/.json`，任务名使用数据集文件名；任务识别、固定种子和工件/批次分组划分；源文件不改写。
- 根据任务匹配项目 `assets/pretrained` 中的模型副本。常用参数：图像尺寸、Batch、轮数、学习率；高级项：种子和划分比例。
- 图像分类与语义分割真实训练、实际Loss/日志、独立评估、停止Worker、原子状态文件与中断识别。停止保留产物，不支持断点续训。
- 模型固定保存到 `models/<任务类别>/<数据集名>/<运行编号>`；包含模型、预处理、类别、参数、划分、评估、日志和SHA-256。只有独立评估完成的分类或语义分割模型可测试。
- 原图批量测试与坏图隔离，`HSmartWindowControlWPF`原生彩色显示、青色预测叠加、透明度调节和原尺寸PNG保存。测试结果保存在 `results/tests`，重启后按模型恢复历史。
- 工作区单实例、关闭时回收Worker。运行环境通过HALCONROOT/HALCONARCH和许可证配置；可用HALCON_TRAINING_HOME指定工作区。

真实训练/测试已验收的任务为**图像分类和语义分割**。分类监控显示验证准确率，原图测试显示预测类别和全类别置信度；独立报告包含混淆矩阵和每类召回率。目标检测、实例分割、OCR和异常任务可读取/识别及匹配基础模型，尚未接通完整训练适配器，界面不冒充可执行。

## 目标检测主动学习（审核/数据准备）

数据集页新增「目标检测 · 主动学习」：导入模型预测 JSON，按检测框置信度、类别得分差及工件/批次分组筛选高价值审核样本；原生图像视口显示检测框，支持修改框坐标和类别、添加/删除框、保存草稿、确认和退回重标。仅人工确认的标签可导出为 COCO（包含原图副本和来源记录），会话可恢复。详见 [docs/ACTIVE_LEARNING_DETECTION.md](docs/ACTIVE_LEARNING_DETECTION.md)。

**边界**：当前目标检测主动学习依赖外部预测 JSON，尚未提供 HALCON 目标检测模型自动批量推理、训练和 mAP 验收；该功能不代表目标检测训练适配器已完成。可用 `dotnet run --project tests/HalconTraining.ActiveLearning.Smoke` 运行无 HALCON 的数据层验证。

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

证据在 `artifacts/wpf-validation`（真实数据/模型/截图不提交）。网页时期的HTML与Node脚本仅保留为历史设计和验证资料，不属于桌面运行依赖。

设计与流程见[WPF_IMPLEMENTATION.md](docs/WPF_IMPLEMENTATION.md)。训练产物和真实图像均不随源代码提交。
分类流程和数据生命周期见 [docs/CLASSIFICATION.md](docs/CLASSIFICATION.md)。
