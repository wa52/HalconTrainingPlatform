# 本机 HALCON 附带模型

检查日期：2026-10-13。依据 HALCONROOT 环境变量定位安装目录，模型在 `HALCONROOT/dl`。下列文件已确认存在，没有复制到项目。文件存在不代表许可、GPU 或训练兼容性已验证。

## 已发现文件
- `pretrained_dl_segmentation_enhanced.hdl`
- `pretrained_dl_segmentation_compact.hdl`
- `pretrained_dl_edge_extractor.hdl`
- `pretrained_dl_classifier_resnet50.hdl`
- `pretrained_dl_classifier_resnet18.hdl`
- `pretrained_dl_classifier_mobilenet_v2.hdl`
- `pretrained_dl_classifier_enhanced.hdl`
- `pretrained_dl_classifier_compact.hdl`
- `pretrained_dl_classifier_alexnet.hdl`
- `pretrained_dl_anomaly_global_context.hdl`
- `pretrained_dl_3d_gripping_point.hdl`
- `pretrained_deep_ocr_recognition.hdl`
- `pretrained_deep_ocr_detection_compact.hdl`
- `pretrained_deep_ocr_detection.hdl`
- `initial_dl_anomaly_medium.hdl`
- `initial_dl_anomaly_large.hdl`

## 任务匹配方案

- 分类：classifier 系列；多标签分类需按 HALCON 模型参数另行配置与校验。
- 语义分割：segmentation_compact / segmentation_enhanced。
- 异常检测：initial_dl_anomaly_medium / large；GC-AD 使用 pretrained_dl_anomaly_global_context。
- OCR：pretrained_deep_ocr_detection（含 compact）与 recognition，组件分开。
- 目标检测：本机没有独立命名的 pretrained_dl_detection 文件。随安装的 `doc_en_US/html/reference/operators/create_dl_model_detection.html` 列出 classifier 骨干网络，并说明 instance_segmentation 参数。正式流程使用 create_dl_model_detection 构建任务模型，不能将分类骨干直接当已训练检测模型使用；实例分割需单独校验支持的骨干与参数。
- edge_extractor 与 3d_gripping_point 不作为当前任务的默认候选。

正式程序扫描环境定位的 dl 目录，不硬编码本机安装路径；读取模型参数后筛选候选，检测等创建式流程同时记录骨干文件与构建参数。仅发现文件时显示“待验证”，不声称已可训练。自定义模型仍可浏览选择。

HTML 界面稿显示本次查到的 Compact/Enhanced 检测骨干选项；更换数据集后清空旧选择，等待真实任务识别再匹配。界面稿没有调用 HALCON 模型加载或构建。

## 最新调整：自动匹配与项目副本

已将 7 个任务默认模型实际复制到项目 `assets/pretrained/`，逐文件验证来源与目标的 SHA-256 一致。具体文件、大小、哈希和任务默认映射记录于 `assets/pretrained/catalog.json`。此前“没有复制”和“保留自定义浏览”的描述为上一版方案，已由本节替代。

模型选择自动进行，项目副本作为基础模型只读使用；每次训练创建独立模型实例，结果写入 models 归档目录，不覆盖基础文件。副本已被项目 .gitignore 排除。任务识别有歧义时保持待识别，模型加载、创建、许可与训练兼容性仍须 Worker 验证；复制成功不表示已能训练。
