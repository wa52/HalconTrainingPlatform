# HALCON / MVTec Deep Learning Tool 数据集格式分析

分析基准：本机 HALCON 24.11.1.1；随安装提供的 `procedures/dl/dl_dataset.hdpl` 文件标记 `halcon_version="24.11.1.0"`。HALCON 在线参考手册对应 24.11.3.0。DLT 工具本身未在本机发现，因此此文档不声称已实测某一 DLT 补丁版的项目文件或往返兼容性。

## 结论先行

HALCON 训练数据不是单一的“数据集文件”。通常由图像文件、任务标注文件和一个 `DLDataset` 字典组成：字典保存数据根路径、类别映射以及每张图的相对路径和标签。训练前，HALCON 再把字典和图像/标注读成 `DLSample`，经 `preprocess_dl_dataset` 转换为模型输入。数据集字典与 DLT 项目是不同的交换/编辑层。

平台首版应使用 HALCON 24.11 的 `read_dict` / `HDict` 和任务专用 `read_dl_dataset_*` 过程作为格式解释器。不要自己反序列化 `.hdict` 二进制，也不要把 DLT `.dltp` 当作公开数据格式。导入先生成只读预览和校验报告，用户确认后才建立平台自己的数据集版本。

## 文件种类与边界

| 文件/目录 | 作用 | 平台导入策略 |
|---|---|---|
| `.dltp` | MVTec Deep Learning Tool 可编辑项目文件。DLT 发布说明确认该扩展名，但其内部结构不是 HALCON `DLDataset` 交换合同。 | 作为 DLT 项目只登记文件路径；交给已安装的 DLT 打开/导出，不自行解析或改写。 |
| `.hdict` | HALCON 二进制 Dictionary 文件；DLT 可从 HALCON Dictionary 建项目并导出标注数据。 | 必须经本机 HALCON `read_dict` / `HDict` 读取，再按方法类型验证键与引用路径。 |
| `.json` | HALCON Dictionary 的 JSON 序列化形式。`read_dict` 自动识别 HALCON 支持的格式。 | 用 HALCON `read_dict` 读取以统一语义；可额外做 JSON 语法预检，但不能取代 HALCON 校验。若字典内含 Region/Iconic Object 或其它不可 JSON 序列化的句柄，应使用 `.hdict`。 |
| 图像文件 | 原图由 `image_dir` + `image_file_name` 组合定位；图像本身一般不嵌入 `DLDataset`。 | 校验文件存在、可读、尺寸/通道/类型符合模型要求；按需索引，不强制复制巨量原图。 |
| 像素掩膜 / 分割图 | 由任务字典中的相对文件名指向外部图像；像素值代表类别/实例标注，语义必须按任务及类别映射解释。 | 读图并校验尺寸对齐、像素 ID、忽略值、调色板/像素类型；不只依据文件名推断标签。 |
| `.hobj` | HALCON Region 序列化文件；HALCON 异常数据读取过程允许以区域文件提供像素级异常真值。 | 通过 HALCON 读取并检查空区域、图像坐标范围及对应关系。 |
| `.hdl` / OCR 模型文件 | 已训练模型或模型组件，不是数据集。 | 在模型/实验流程中导入；不作为 Dataset Import 的输入。 |

HALCON 手册说明 `DLDataset` 的核心通用键为 `image_dir`、`class_names`、`class_ids` 和 `samples`；预处理之后还会出现 `preprocess_param`、`dlsample_dir` 和样本的 `dlsample_file_name`。样本常见通用键为 `image_id`、`image_file_name`、`split`。图像数据和图标注大多仍在字典之外，路径相对各自根目录。[HALCON 24.11 Model](https://www.mvtec.com/doc/halcon/2411/en/toc_deeplearning_model.html)

`read_dict` 可读 `.hdict` 和 `.json`。但它们不是无损互换格式：JSON 不支持 HALCON iconic objects 与若干非 Dictionary handles；JSON 数组的默认表示还是 HALCON Dictionary，可配置成 tuple 或 tuple-if-possible，而后者也可能因空数组/嵌套数组造成类型形状差异。因此 JSON 必须经过任务 schema 校验，含区域对象/句柄的任务数据优先使用 `.hdict`。 [HALCON 24.11 read_dict](https://www.mvtec.com/doc/halcon/2411/en/read_dict.html) · [write_dict](https://www.mvtec.com/doc/halcon/2411/en/write_dict.html)

## DLT 与 HALCON 24.11 的任务数据

MVTec 当前 DLT 产品工作流列出分类、Global Context Anomaly Detection、目标检测、实例分割、语义分割和 Deep OCR；OCR 的检测与识别分别训练，实际是两条不同的数据合同。HALCON 24.11 另外还提供 anomaly detection、multi-label classification 等能力。不能据此假设这些模型类型都能由目标 DLT 版本直接创建、训练或导出；版本/许可/工具界面必须单独核对。[DLT 功能与任务](https://www.mvtec.com/products/deep-learning-tool/features-workflows) · [HALCON 24.11 Model](https://www.mvtec.com/doc/halcon/2411/en/toc_deeplearning_model.html)

| 任务 | HALCON/DLT 数据标注 | 24.11 读取入口 | 重要校验 |
|---|---|---|---|
| 分类 | 一图一个类别。HALCON 过程支持从类别目录、文件名或移除末尾数字/下划线后的文件名生成标签；目录模式使用最后一级目录名。DLDataset 样本用 `image_label_id` 指向 `class_ids`。 | `read_dl_dataset_classification` | 类名稳定、类 ID 映射正确；一图一个标签；目录共同父路径；训练/验证/测试按工件/批次分组防泄漏。 |
| 异常检测 AD | `good`/`ok` 表示正常，其它类目录表示异常。像素级异常真值可选；可使用图像或 `.hobj` Region 文件，样本有 `anomaly_label` 和可选 `anomaly_file_name`。 | `read_dl_dataset_anomaly` | 正常目录命名；异常标签正负映射；真值文件与图像名关联准确；没有像素真值时不可报告像素级分割指标。 |
| Global Context Anomaly Detection | 同样按图像级 Good/Anomaly 标签组织；在 HALCON DLDataset 中归入 anomaly 数据合同，具有 `anomaly_label`，可带 `anomaly_file_name` 真值区域。 | `read_dl_dataset_anomaly` | 不要把 GC-AD 与传统像素级异常检测、DLT 文件格式混为一谈；具体模型适配参数另行检查。 |
| 目标检测 | 一个样本对应一个原图，标注为类别 + 框。轴对齐框使用 `bbox_row1/col1/row2/col2`；旋转框使用中心 `bbox_row/col`、角度 `bbox_phi`、半边长 `bbox_length1/2`。类别使用 `bbox_label_id`。 | DLT `.hdict` 经 HALCON DLDataset 流程；标准 COCO JSON 可用 `read_dl_dataset_from_coco` | HALCON 的 COCO reader 只声明 Object Detection 且 `instance_type=rectangle1`；不要用它导入旋转框。坐标要保留像素中心、亚像素精度约定。 |
| 实例分割 | 每个实例有类别、定位框/区域和实例 mask；HALCON `DLDataset` 目标检测样本中的 `mask` 是 Region tuple，标记实例区域。 | DLT 导出的 HALCON Dictionary；COCO reader 是否足够需按其受限能力验证 | 不可把多实例区域压成单个语义类别图；重叠实例和实例 ID 必须保留。 |
| 语义分割 | 原图 + 对齐的 segmentation 图；数据集包含 `segmentation_dir`，样本通过 `segmentation_file_name` 找掩膜图。 | `read_dl_dataset_segmentation` | `ClassNames` 与 `ClassIDs` 数量相同且 ID 唯一；原图和 mask 成对、尺寸对齐、mask 像素只使用允许类别 ID/忽略值。 |
| Deep OCR 检测组件 | 场景图中文字实例用旋转框；类别映射为 `word`、`char`、`ignore`，文字内容可在 `label_custom_data[*].text`；标准化后是 bbox tuple 与 `word`。 | `read_dl_dataset_ocr_detection` | 检查非法/空 word 标签；逐实例验证旋转框和对应文本。检测训练并非普通分类标签。 |
| Deep OCR 识别组件 | 两种数据形态：场景图 + 旋转 word 框 + 文本；或已经裁好的单词图 + 每图 `word` 字符串。后一种每张图只包含一个 word。 | `read_dl_dataset_ocr_recognition` | 不混用检测与识别目标；校验裁剪是否恰为单词、字符集/文本编码和旋转框标注；HALCON 只重训一个 Deep OCR 组件一次。 |
| Multi-label classification | 每张图可以有多个类别，`image_label_ids` 是多个 class ID。 | HALCON 24.11 的任务流程；并非当前 DLT 产品页面列出的训练任务 | 平台自定义 HALCON 流程需要单独适配，不可按普通单标签 `read_dl_dataset_classification` 导入。 |

任务数据细节以本机 24.11.1.0 的 `procedures/dl/dl_dataset.hdpl` 注释、实现和异常检查为准。它确认：分类、异常、语义分割可从图像目录和标签目录构建 DLDataset；OCR 过程先调用 `read_dict` 再做任务格式归一化；COCO 入口只支持 Object Detection。HALCON 24.11 Model 文档列出的样本键和几何含义见前述参考手册。

## 建议的程序导入管线

```text
选择 .hdict/.json 或目录
  → 文件/权限/数量预检（只读）
  → HALCON Worker: read_dict 或任务专用 read_dl_dataset_*
  → 任务识别与 DLDataset schema 校验
  → 解析所有相对路径、图像/掩膜、类别、坐标和 split
  → 采样预览 + 全量快速校验 + 可选 HALCON 读图校验
  → 展示错误清单和警告
  → 用户确认创建不可变 DatasetVersion
  → 按模型参数 preprocess_dl_dataset
  → 保存处理参数与来源摘要并进入 Experiment
```

- App 不直接引用 `HDict`、`HObject` 等 HALCON 句柄。HALCON Worker 负责读取文件、方法专用解析、路径解析、读图校验与预处理；通过版本化 JSON/IPC DTO 回传轻量清单、统计和错误。图像像素大批量传输用文件引用/缓存，不在 IPC 内嵌大图。
- 内部统一 Dataset Manifest 保留源文件 hash、原始键、类别 ID、分割策略、每样本相对路径、标签/几何、附加 DLT 元数据；任务 schema 作为 discriminated union，避免检测框、实例 mask、OCR word 等互相丢失。
- `.hdict` 导入采用只读读取；编辑产生新版本导出副本，不覆盖源文件。保留未识别键以支持 DLT 往返兼容。MVTec DLT 版本说明明确指出，导入 Dictionary 的附加数据会保存并可再导出，但删除类别/标注后这类自定义键的一致性不受保证。[DLT Release Notes](https://www.mvtec.com/products/deep-learning-tool/release-notes)
- 路径解析兼容 Windows `\\` 与 HALCON 字典中相对路径；记录原始路径与解析绝对路径，避免移动项目后静默错指。创建平台项目时支持“引用原数据”或“复制到项目托管目录”，复制模式需显示空间预估并校验 SHA-256。
- 数据划分存放在样本 `split`，值为 `train`、`validation`、`test`。生成 split 时以工件/序列/生产批次做 group split，再冻结到数据集版本；重导入保留源 split，冲突由用户选择策略。
- 导入阶段输出 `DatasetImportReport`：任务类型、HALCON/DLT schema 版本猜测、图像数、类别映射、split 统计、缺失文件、坏图、标签异常、尺寸不符、路径越界、无法识别字段及来源 hash。发现数据集是预处理后的 DLDataset 时识别 `preprocess_param`/`dlsample_dir`，避免重复预处理。

## DLT 项目文件的处理边界

DLT 的 `.dltp` 是可编辑项目，不是 `.hdict` 的另一种扩展名。官方发布说明确认扩展名，并说明可以从 HALCON Dictionary 建立 DLT 项目、导入/导出 HALCON Dictionary；但公开 HALCON DLDataset 合同没有定义 `.dltp` 的内部结构。[DLT Release Notes](https://www.mvtec.com/products/deep-learning-tool/release-notes)

因此 V1 不逆向 `.dltp` 私有容器。对话中未提供具体 `.dltp` / `.hdict` 样本；在 Windows 卸载登记、本机 MVTec 安装目录和 HALCON 安装目录中未发现 MVTec Deep Learning Tool 或 `.hdict` 示例。REA MCP 注册已写入 Codex 配置并由 `rea doctor` 确认对齐，但当前 Codex 进程尚未重载配置，所以 REA 工具还未进入本会话。后续取得真实文件后，优先用 HALCON 24.11 `read_dict` / 对应 reader 建立 golden fixtures，再用 DLT 对照打开、导出，验证 round-trip。只有当 DLT 项目容器自动化确为硬需求、且有用户授权的真实项目副本时，才用 REA 对 `.dltp` 做只读结构检查。

## 已查证来源

- 本机：`%HALCONROOT%/procedures/dl/dl_dataset.hdpl`，HALCON 标头 24.11.1.0；本机 HALCON 运行时是 24.11.1.1。
- [HALCON 24.11 Deep Learning / Model](https://www.mvtec.com/doc/halcon/2411/en/toc_deeplearning_model.html)：DLDataset/DLSample 键、几何字段与支持的 model type。
- [HALCON 24.11 Deep OCR](https://www.mvtec.com/doc/halcon/2411/en/toc_ocr_deepocr.html)：OCR detection/recognition 数据要求与组件分别重训。
- [MVTec DLT features and workflows](https://www.mvtec.com/products/deep-learning-tool/features-workflows)：DLT 的标注、训练任务和数据管理边界。
- [MVTec DLT release notes](https://www.mvtec.com/products/deep-learning-tool/release-notes)：`.dltp` 扩展名、HDICT 导入导出与元数据往返行为。
- [HALCON 24.11 `read_dict`](https://www.mvtec.com/doc/halcon/2411/en/read_dict.html)：HALCON Dictionary 文件读取；HDICT 是 HALCON 二进制 Dictionary，JSON 也是受支持的 Dictionary 存储形式。
