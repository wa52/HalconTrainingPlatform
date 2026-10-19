#!/usr/bin/env python3
"""Ultralytics YOLO detection fine-tuning and prediction for HALCON WPF active learning.

prepare: convert COCO training snapshot including empty-label negatives to YOLO labels (Python stdlib only).
train: fine-tune pretrained detection weights on >=5 training images (NOT validated).
predict: write schemaVersion=1 proposals for human review, never ground truth.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import shutil
import sys
from pathlib import Path


def emit(stage: str, **fields: object) -> None:
    print(json.dumps({"stage": stage, **fields}, ensure_ascii=False), flush=True)


def prepare_coco(coco_file: str, output_folder: str) -> dict:
    source = Path(coco_file).resolve()
    target = Path(output_folder).resolve()
    dataset = json.loads(source.read_text(encoding="utf-8"))
    images = dataset.get("images", [])
    categories = dataset.get("categories", [])
    annotations = dataset.get("annotations", [])
    if not isinstance(images, list) or len(images) < 5:
        raise ValueError("至少需要 5 张人工确认的图片才能进行预训练模型微调。")
    if not isinstance(categories, list) or not categories:
        raise ValueError("缺少目标检测类别。")
    ids = [item["id"] for item in categories]
    names = [item["name"] for item in categories]
    if len(ids) != len(set(ids)) or any(not isinstance(n, str) or not n.strip() for n in names):
        raise ValueError("类别 ID 或名称无效。")
    if len(names) != len(set(names)):
        raise ValueError("重复的类别名称。")
    index = {id_: n for n, id_ in enumerate(ids)}
    image_info = {}
    for record in images:
        image_id = record["id"]
        filename = record["file_name"]
        w, h = record["width"], record["height"]
        if (not isinstance(image_id, int) or image_id in image_info
                or not isinstance(filename, str) or Path(filename).name != filename
                or "\\" in filename or filename in ("", ".", "..")
                or not isinstance(w, int) or not isinstance(h, int) or min(w, h) < 1):
            raise ValueError("图片 ID、名称或尺寸无效。")
        image_file = source.parent / "images" / filename
        if not image_file.is_file():
            raise FileNotFoundError("训练图片不存在：" + str(image_file))
        image_info[image_id] = (filename, w, h, image_file)
    boxes = {image_id: [] for image_id in image_info}
    positive_categories = set()
    for record in annotations:
        image_id, category_id = record["image_id"], record["category_id"]
        if image_id not in image_info or category_id not in index or record.get("iscrowd", 0):
            raise ValueError("检测框引用无效的图片/类别，或标记为 crowd。")
        rect = record["bbox"]
        if len(rect) != 4 or any(not isinstance(n, (int, float)) or not math.isfinite(n) for n in rect):
            raise ValueError("检测框坐标无效。")
        x, y, width, height = rect
        _, image_width, image_height, _ = image_info[image_id]
        if (x < 0 or y < 0 or width <= 0 or height <= 0
                or x + width > image_width or y + height > image_height):
            raise ValueError("检测框越界。")
        boxes[image_id].append(
            f"{index[category_id]} {(x + width / 2) / image_width:.8f} "
            f"{(y + height / 2) / image_height:.8f} "
            f"{width / image_width:.8f} {height / image_height:.8f}"
        )
        positive_categories.add(category_id)
    if set(ids) != positive_categories:
        missing = [entry["name"] for entry in categories if entry["id"] not in positive_categories]
        raise ValueError("以下类别没有任何目标框：" + "、".join(missing))

    # Every snapshot is immutable; image names are already unique in COCO exporter.
    images_out, labels_out = target / "images" / "train", target / "labels" / "train"
    images_out.mkdir(parents=True, exist_ok=True)
    labels_out.mkdir(parents=True, exist_ok=True)
    for image_id, (filename, _, _, image_file) in image_info.items():
        shutil.copy2(image_file, images_out / filename)
        (labels_out / (Path(filename).stem + ".txt")).write_text(
            "\n".join(boxes[image_id]) + ("\n" if boxes[image_id] else ""), encoding="utf-8")
    # JSON is valid YAML syntax, and avoids an extra dependency for conversion.
    configuration = {
        "path": str(target),
        "train": "images/train",
        "val": "images/train",  # Required by loader, NOT an independent validation set.
        "nc": len(names),
        "names": {n: name for n, name in enumerate(names)},
    }
    (target / "data.yaml").write_text(json.dumps(configuration, ensure_ascii=False, indent=2), encoding="utf-8")
    metadata = {
        "sourceCoco": str(source),
        "sourceCocoSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "classIds": ids,
        "classNames": names,
        "trainingImages": len(images),
        "trainingBoxes": sum(map(len, boxes.values())),
        "warning": "Train-only seed dataset. Validation/test metrics are NOT available.",
    }
    (target / "source.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8")
    return metadata


def require_ultralytics():
    try:
        from ultralytics import YOLO
        import torch
        return YOLO, torch
    except ImportError as error:
        raise RuntimeError(
            "缺少 YOLO 环境。请在独立 Python 环境安装 torch 和 ultralytics，"
            "然后用 HALCON_TRAINING_PYTHON 指向该 python.exe。"
        ) from error


def train(args):
    emit("初始化训练环境", pid=os.getpid())
    YOLO, torch = require_ultralytics()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if args.epochs < 1 or args.batch < 1 or args.imgsz < 64 or not (0 < args.lr < 1):
        raise ValueError("Epoch、Batch、尺寸和学习率参数无效。")
    dataset_folder = output / "dataset"
    info = prepare_coco(args.coco, str(dataset_folder))
    if args.batch > info["trainingImages"]:
        raise ValueError("Batch 不能大于训练图片数量。")
    emit("开始微调", trainingImages=info["trainingImages"], pretrained=args.model)
    detector = YOLO(args.model)

    def on_epoch(trainer):
        loss = getattr(trainer, "tloss", None)
        emit("训练", epoch=trainer.epoch + 1, epochs=args.epochs,
             loss=float(loss.sum().item()) if loss is not None else None)

    detector.add_callback("on_train_epoch_end", on_epoch)
    detector.train(
        data=str(dataset_folder / "data.yaml"), epochs=args.epochs,
        imgsz=args.imgsz, batch=args.batch, device=0 if torch.cuda.is_available() else "cpu",
        optimizer="AdamW", lr0=args.lr, val=False, plots=False,
        project=str(output), name="training", exist_ok=True, workers=0,
        # Minimize aggressive augmentations for tiny industrial datasets.
        mosaic=0.0, mixup=0.0, copy_paste=0.0, fliplr=0.5,
    )
    last = output / "training" / "weights" / "last.pt"
    if not last.is_file():
        raise RuntimeError("YOLO 未产生 last.pt，训练未成功。")
    trained = output / "model.pt"
    shutil.copy2(last, trained)
    manifest = {
        "status": "bootstrap_unvalidated",
        "engine": "ultralytics-yolo",
        "modelFile": str(trained),
        "pretrained": args.model,
        "epochs": args.epochs, "batch": args.batch,
        "imageSize": args.imgsz, "learningRate": args.lr,
        **info,
    }
    (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    emit("训练完成", modelFile=str(trained), status="bootstrap_unvalidated")


def predict(args):
    emit("初始化预测环境")
    max_detections = args.max_det
    if not 1 <= max_detections <= 300:
        raise ValueError("每图最多预测框数需在 1～300 之间。")
    confidence = args.confidence
    if not math.isfinite(confidence) or not 0 < confidence <= 1:
        raise ValueError("预测阈值需大于 0 且不超过 1。")
    YOLO, torch = require_ultralytics()
    model_file = Path(args.model).resolve()
    if not model_file.is_file():
        raise FileNotFoundError("找不到 YOLO 模型：" + str(model_file))
    paths = json.loads(Path(args.images_json).read_text(encoding="utf-8"))
    if not isinstance(paths, list) or not paths or len(paths) != len(set(paths)):
        raise ValueError("原图列表为空或有重复。")
    detector = YOLO(str(model_file))
    model_names = detector.names
    if isinstance(model_names, dict):
        names = {int(k): str(v) for k, v in model_names.items()}
    else:
        names = {idx: str(name) for idx, name in enumerate(model_names)}
    ordered = sorted(names)
    if ordered != list(range(len(ordered))):
        raise ValueError("模型类别索引不是连续的 0..N-1。")

    metadata_file = model_file.parent / "manifest.json"
    original_ids = list(range(len(names)))
    if metadata_file.is_file():
        manifest = json.loads(metadata_file.read_text(encoding="utf-8"))
        ids = manifest.get("classIds")
        expected_names = manifest.get("classNames")
        if (isinstance(ids, list) and isinstance(expected_names, list)
                and len(ids) == len(names)
                and all(expected_names[n] == names[n] for n in ordered)):
            original_ids = ids

    output = Path(args.output_json).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    proposals, errors = [], []
    for i, raw in enumerate(paths):
        path = Path(raw).resolve()
        try:
            if not path.is_file():
                raise FileNotFoundError(str(path))
            response = detector.predict(
                source=str(path), conf=confidence, iou=0.7, max_det=max_detections,
                imgsz=args.imgsz, device=0 if torch.cuda.is_available() else "cpu",
                verbose=False,
            )[0]
            boxes = []
            if response.boxes is not None:
                xyxy = response.boxes.xyxy.cpu().tolist()
                cls = response.boxes.cls.cpu().tolist()
                conf = response.boxes.conf.cpu().tolist()
                for coords, cls_index, score in zip(xyxy, cls, conf):
                    class_id = int(cls_index)
                    if class_id not in names:
                        raise ValueError("模型产生未知类别 ID。")
                    left, top, right, bottom = [float(x) for x in coords]
                    if right <= left or bottom <= top or not 0 <= score <= 1:
                        continue
                    boxes.append({
                        "classId": original_ids[class_id], "confidence": float(score),
                        "col1": max(0, min(left, float(response.orig_shape[1]))),
                        "row1": max(0, min(top, float(response.orig_shape[0]))),
                        "col2": max(0, min(right, float(response.orig_shape[1]))),
                        "row2": max(0, min(bottom, float(response.orig_shape[0]))),
                    })
            boxes = sorted(boxes, key=lambda box: box['confidence'], reverse=True)[:max_detections]
            proposals.append({
                "imagePath": str(path),
                "width": int(response.orig_shape[1]), "height": int(response.orig_shape[0]),
                "boxes": boxes,
            })
        except (OSError, ValueError, RuntimeError) as error:
            errors.append({"imagePath": str(path), "error": str(error)})
        emit("预测", processed=i+1, total=len(paths), successful=len(proposals), failed=len(errors))
    if not proposals:
        raise RuntimeError("全部图片预测失败，请检查图片和模型环境。")
    summary = {"processed": len(paths), "detectedImages": sum(bool(image['boxes']) for image in proposals),
               "emptyImages": sum(not image['boxes'] for image in proposals),
               "totalBoxes": sum(len(image['boxes']) for image in proposals), "failed": len(errors)}
    payload = {
        "schemaVersion": 1,
        "predictionOptions": {"maxDetections": max_detections, "confidence": confidence, "iou": 0.7},
        "summary": summary,
        "modelId": "yolo:" + hashlib.sha256(model_file.read_bytes()).hexdigest()[:12],
        "classes": [{"id": original_ids[i], "name": names[i]} for i in ordered],
        "images": proposals,
    }
    output.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    output.with_suffix(".errors.json").write_text(json.dumps(errors, ensure_ascii=False, indent=2), encoding="utf-8")
    emit("预测完成", successful=len(proposals), **summary, outputJson=str(output))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    actions = parser.add_subparsers(dest="action", required=True)
    p = actions.add_parser("prepare")
    p.add_argument("--coco", required=True)
    p.add_argument("--output", required=True)
    t = actions.add_parser("train")
    t.add_argument("--coco", required=True)
    t.add_argument("--output", required=True)
    t.add_argument("--model", default="yolo26n.pt")
    t.add_argument("--epochs", type=int, default=40)
    t.add_argument("--batch", type=int, default=2)
    t.add_argument("--imgsz", type=int, default=640)
    t.add_argument("--lr", type=float, default=0.001)
    q = actions.add_parser("predict")
    q.add_argument("--model", required=True)
    q.add_argument("--images-json", required=True)
    q.add_argument("--output-json", required=True)
    q.add_argument("--imgsz", type=int, default=640)
    q.add_argument("--max-det", type=int, default=300)
    q.add_argument("--confidence", type=float, default=0.05)
    args = parser.parse_args(argv)
    try:
        if args.action == "prepare":
            emit("准备完成", **prepare_coco(args.coco, args.output))
        elif args.action == "train":
            train(args)
        elif args.action == "predict":
            predict(args)
        return 0
    except Exception as error:
        print(f"{type(error).__name__}: {error}", file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    sys.exit(main())
