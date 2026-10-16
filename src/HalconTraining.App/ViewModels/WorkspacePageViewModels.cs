namespace HalconTraining.App.ViewModels;

public sealed record WorkspaceActionViewModel(string Title, string Description, string Status);

public abstract class WorkspacePageViewModel(string id, string title, string description) : ObservableModel
{
    private bool _isSelected;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public abstract IReadOnlyList<WorkspaceActionViewModel> Actions { get; }
}

public sealed class ProjectOverviewViewModel() : WorkspacePageViewModel("overview", "项目总览", "管理训练项目、任务类型和运行环境。")
{
    public override IReadOnlyList<WorkspaceActionViewModel> Actions =>
    [
        new("项目与任务", "创建独立项目，选择检测、分类、分割、OCR、匹配等 HALCON 2D 任务。", "框架已就绪"),
        new("环境门禁", "在启动训练前核验 HALCON 版本、GPU 支持和任务级能力。", "环境采集已接入")
    ];
}

public sealed class DatasetViewModel() : WorkspacePageViewModel("dataset", "数据集", "导入图像、检查数据质量，并生成可追溯的数据版本。")
{
    public override IReadOnlyList<WorkspaceActionViewModel> Actions =>
    [
        new("导入图像", "支持目录导入、常见图像格式与大数据集后台索引。", "待实现"),
        new("数据划分", "按产品、批次或采集序列分组，避免训练集与验证集泄漏。", "待实现"),
        new("版本冻结", "保存图像清单、标签、划分和校验和，供实验复现。", "待实现")
    ];
}

public sealed class AnnotationViewModel() : WorkspacePageViewModel("annotation", "标注复核", "依据不同任务选择标注工具，并支持审核和问题回流。")
{
    public override IReadOnlyList<WorkspaceActionViewModel> Actions =>
    [
        new("分类标签", "图像级类别与质量检查。", "待实现"),
        new("检测框 / 区域", "矩形框、区域和缺陷轮廓标注。", "待实现"),
        new("OCR 与关键点", "字符区域、文本内容、姿态和关键点标注。", "待实现")
    ];
}

public sealed class ExperimentViewModel() : WorkspacePageViewModel("experiment", "实验配置", "创建可复现配置，并在运行前检查参数与资源约束。")
{
    public override IReadOnlyList<WorkspaceActionViewModel> Actions =>
    [
        new("任务适配器", "首版规划覆盖 10 类 2D 任务；每类能力需逐项验收。", "待实现"),
        new("参数预检", "校验数据、模型、图像尺寸、显存和许可证要求。", "待实现"),
        new("配置快照", "记录 HALCON 版本、参数、数据版本和代码适配器版本。", "待实现")
    ];
}

public sealed class TrainingMonitorViewModel() : WorkspacePageViewModel("training", "训练监控", "观察作业状态、指标、日志，并支持安全取消和故障恢复。")
{
    public override IReadOnlyList<WorkspaceActionViewModel> Actions =>
    [
        new("作业状态机", "约束排队、准备、训练、验证、取消和终态转换。", "已实现"),
        new("实时训练事件", "呈现进度、损失、验证指标、显存和 Worker 日志。", "待实现"),
        new("断点与恢复", "通过独立 Worker 隔离 HALCON native 生命周期并恢复作业。", "待实现")
    ];
}

public sealed class EvaluationViewModel() : WorkspacePageViewModel("evaluation", "评估对比", "查看整体指标、逐图预测和错误样本，支持模型间对比。")
{
    public override IReadOnlyList<WorkspaceActionViewModel> Actions =>
    [
        new("任务指标", "按检测、分类、分割、OCR 等任务展示适配指标。", "待实现"),
        new("错误样本", "筛选漏检、误检、错分及低置信样本。", "待实现"),
        new("可视化对比", "对照原图、标签、预测结果和阈值变化。", "待实现")
    ];
}

public sealed class ModelLibraryViewModel() : WorkspacePageViewModel("models", "模型管理", "验证模型包完整性、兼容性与推理能力，再进行交付。")
{
    public override IReadOnlyList<WorkspaceActionViewModel> Actions =>
    [
        new("模型注册", "管理模型版本、任务类型、来源实验和数据集版本。", "待实现"),
        new("导出与验证", "验证 HALCON 24.11 目标运行时加载和推理。", "待实现"),
        new("部署包", "生成包含预处理、类别、阈值和校验信息的交付包。", "待实现")
    ];
}
