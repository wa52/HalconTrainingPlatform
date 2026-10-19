# 目标检测训练耗时与模型复用

## 操作

数据集页「目标检测 · 标注与训练」的四个核心参数保持直接可编辑。

- 默认勾选「HALCON 优先复用已有模型」。合并训练匹配上次完成的合并模型，本类 HALCON 预标注训练只匹配本类模型；不将其他文件夹或 YOLO 权重用于 HALCON 微调。
- 类别 ID、名称、顺序与图像尺寸全部一致才会自动匹配。信息不足或不匹配时，界面明确提示从基础模型开始。Worker 再核对 `.hdl` 的原生参数，不能通过修改 JSON 绕过检查。
- 取消勾选可从基础模型训练。继续微调时，Epoch 是**本轮追加轮数**，不是累计轮数；不会自动缩减用户设置的轮数或修改 Batch、学习率、尺寸。
- 「更多操作 → 合并训练：选择续训 .hdl」可选择已完成轮次的 `model.hdl`，例如中断任务的检查点。需保留同目录 `preprocess.hdict`；原有归一化、尺寸、像素范围和框类型一并检查或继承。此选项只作用于合并训练。
- 微调保存到新运行目录，不覆盖来源模型。只复用权重及配套预处理，**不声明精确恢复优化器、随机数状态和原任务轮次**。

## 耗时与中断

每批输出真实读取、预处理、计算耗时和张/秒；界面显示速度，详细数据在日志中。首批训练包含 GPU 预热，会明确提示等待。

每个完整 Epoch 原子替换 `model.hdl`，并写入 `checkpoint.json`、`performance.json`。停止不登记为完成模型；后续可手选检查点继续微调。`training_params.json` 记录本轮参数、来源模型和数据 SHA-256；最终 `manifest.json` 保存来源、耗时和逐轮性能。`training.log` 在 Worker 中持续写入并刷新，`worker.log` 保存正常退出时的标准输出/错误，不相互覆盖。

所有训练仍只使用已确认图片导出的 train 划分，validation/test 不进入优化器。增加或减少 Epoch、改变 Batch/尺寸都可能影响精度，不能以训练 Loss 代替独立 mAP。

## 本机基准（2026-10-19）

HALCON 24.11、RTX 5060 Laptop 8 GB、256×256 检测模型。同一已训练模型、同一顺序的 24 张 train 图像，预热后重复 3 次，共 72 张。测量的是稳态处理，不含首次 CUDA/GPU 初始化。

| 测试 | 读取及预处理 | GPU 训练 | 吞吐量 | 结论 |
|---|---:|---:|---:|---|
| Batch 8 基线 | 0.159 秒 | 11.131 秒 | 6.38 张/秒 | 数据准备约占 1.4% |
| Batch 8 层融合 | 0.216 秒 | 11.132 秒 | 6.34 张/秒 | 层数 131→131，无有效收益，不在正式训练中启用 |
| Batch 4 | — | — | 不作比较 | 期间用户启动正式训练，存在 GPU 竞争，排除该结果 |

当前不引入大规模图片缓存，也不声称已获得固定倍数提速。模型复用提供减少重复训练的基础；达到相同独立精度所需轮数和总时间仍需对比。30 轮正式任务运行期间停止额外 GPU 测试；该任务完成后，已在隔离目录完成两轮真实 GPU 微调、划分隔离、检查点、性能报告及来源文件 SHA-256 不变检查。

## 可重复验证

数据层、模型匹配及进度解析：

```powershell
dotnet run --project tests/HalconTraining.ActiveLearning.Smoke -c Debug -p:NuGetAudit=false
```

原生模型兼容性与预处理检查，不训练（当前冒烟工具使用 256×256 水平框检测模型）：

```powershell
dotnet run --project tests/HalconTraining.DetectionWarmStart.Smoke -c Debug -p:NuGetAudit=false -- --model <已完成模型或检查点.hdl> --dataset <检测数据集.hdict> --output <新的验证目录>
```

同一命令追加 `--train` 才进行两轮真实 GPU 微调，并检查数据划分隔离、检查点、性能报告、来源文件未改变。该测试只创建隔离产物，不登记到正式模型库；检测到正式 Worker 时拒绝执行。原生检查和两轮真实 GPU 微调均已通过；本次使用 24 张 train 图像，另外 6 张 validation 和 6 张 test 未进入优化器。两轮平均训练 Loss 约 0.07164、0.06597，这不是独立精度指标。

`tests/HalconTraining.DetectionPerformance.Smoke` 支持 `--dataset`、`--preprocess`、`--model`、`--batch`、`--repeats`、`--output`，以及实验性的 `--cached/--fusion`。不要与其他 GPU 任务同时运行；检测到正式 Worker 后会停止。基准不保存模型，不改写源图或标签。

WPF 控件绑定和最小窗口验证：使用独立 `HALCON_TRAINING_HOME`，复制已有会话到独立工作区后运行 `--validate-detection-optimization --validation-output <证据目录>`；不启动训练。已通过自动匹配、重新训练切换、尺寸改变即时反馈、1120×780 核心操作与原图可见检查。
