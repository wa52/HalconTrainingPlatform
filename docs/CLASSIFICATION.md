# 图像分类训练

数据流：原始 HALCON 字典 → 读取 class_ids/class_names 和 image_label_id → 按现有统一规则划分训练/验证/测试 → 自动匹配项目内 compact 分类模型 → 按 class_names 重建分类输出层 → 官方预处理 → 真实批次训练 → 每轮完整验证集准确率 → 新进程重载模型独立评估 → 模型包校验归档 → 原图测试。

源字典和原图只读。原始类别 ID 映射到模型的连续类别 ID，类别名称和源 ID 一同保存在 manifest。训练前拒绝未知标签、缺失图片、空集合或训练集缺类。验证和测试不更新权重；独立报告保存准确率、Top-1 错误率、混淆矩阵和各类别召回率。

HALCON 句柄只存在于 Worker 内。训练事件传递数值、图片路径、真实/预测类别及置信度；WPF 使用原生 HSmartWindow 显示原图。分类结果没有分割掩膜，界面显示类别和置信度，禁用掩膜叠加保存。输出固定在 models/classification/<数据集名>/<运行版本>。停止时杀掉独立 Worker 并记录中断，不将部分结果发布为已验证模型。

参考本机 HALCON 24.11 Classification/classify_pill_defects_deep_learning_1_preprocess.hdev、_2_train.hdev、_4_infer.hdev，以及官方 TrainDlModelBatch / ApplyDlModel 文档。
