"""Fine-tune YOLOv8n-cls on SleepWatch asleep/awake crops.

Uses the already-prepared dataset in datasets/sleepwatch_cls and the local
pretrained weights. Prints validation metrics and sweeps a decision threshold
for the "asleep" class so precision can be tuned without retraining.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path

import numpy as np
import torch
from ultralytics import YOLO

BASE = Path(__file__).resolve().parent / "training"
PRETRAINED = Path(os.environ.get("SLEEPWATCH_PRETRAINED", BASE / "weights" / "yolov8n-cls.pt"))
RUN = BASE / "runs"

_parser = argparse.ArgumentParser()
_parser.add_argument("--data", type=Path, default=BASE.parent / "datasets" / "sleepwatch_cls")
_parser.add_argument("--name", default="sleepwatch_cls")
_args, _ = _parser.parse_known_args()
DATA = _args.data
RUN_NAME = _args.name
OUT = BASE / "results" / RUN_NAME
OUT.mkdir(parents=True, exist_ok=True)


def jsonable(value):
    if hasattr(value, "item"):
        try:
            return value.item()
        except Exception:
            pass
    if isinstance(value, (list, tuple)):
        return [jsonable(v) for v in value]
    if isinstance(value, dict):
        return {str(k): jsonable(v) for k, v in value.items()}
    if isinstance(value, (np.integer, np.floating)):
        return value.item()
    return str(value)


def binary_metrics(y_true: np.ndarray, y_pred: np.ndarray) -> dict:
    # positive class = asleep (0)
    tp = int(np.sum((y_true == 0) & (y_pred == 0)))
    fp = int(np.sum((y_true == 1) & (y_pred == 0)))
    fn = int(np.sum((y_true == 0) & (y_pred == 1)))
    tn = int(np.sum((y_true == 1) & (y_pred == 1)))
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    accuracy = (tp + tn) / len(y_true) if len(y_true) else 0.0
    specificity = tn / (tn + fp) if tn + fp else 0.0
    return {
        "tp": tp, "fp": fp, "fn": fn, "tn": tn,
        "precision_asleep": round(precision, 4),
        "recall_asleep": round(recall, 4),
        "f1_asleep": round(f1, 4),
        "accuracy": round(accuracy, 4),
        "specificity_awake": round(specificity, 4),
    }


def main() -> None:
    use_cuda = torch.cuda.is_available()
    device = 0 if use_cuda else "cpu"
    epochs = 80 if use_cuda else 45
    batch = 32 if use_cuda else 16
    device_name = torch.cuda.get_device_name(0) if use_cuda else "CPU"
    print(f"device = {device} ({device_name}), epochs={epochs}, batch={batch}")

    model = YOLO(str(PRETRAINED))
    print("class names before training:", model.names)

    model.train(
        data=str(DATA),
        epochs=epochs,
        imgsz=224,
        batch=batch,
        device=device,
        patience=15,
        workers=0,
        project=str(RUN),
        name=RUN_NAME,
        exist_ok=True,
        verbose=True,
    )

    best = RUN / RUN_NAME / "weights" / "best.pt"
    if not best.exists():
        raise FileNotFoundError(f"best weights not found: {best}")
    model = YOLO(str(best))
    print("class names after training:", model.names)

    val_metrics = model.val(data=str(DATA), split="val", device=device, verbose=False)
    val_summary = jsonable(val_metrics.results_dict)
    (OUT / "ultralytics_val.json").write_text(
        json.dumps(val_summary, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    # Collect asleep probabilities on validation set for threshold tuning.
    val_paths = sorted((DATA / "val" / "asleep").glob("*.jpg")) + sorted(
        (DATA / "val" / "awake").glob("*.jpg")
    )
    y_true = np.array([0] * len(list((DATA / "val" / "asleep").glob("*.jpg")))
                      + [1] * len(list((DATA / "val" / "awake").glob("*.jpg"))))
    names = model.names
    asleep_idx = next(i for i, n in names.items() if str(n).lower() in {"asleep", "sleep", "睡觉"})
    results = model.predict(
        source=[str(p) for p in val_paths], device=device, batch=32, verbose=False
    )
    p_asleep = np.array([float(r.probs.data[asleep_idx].cpu()) for r in results])

    sweep = []
    for threshold in np.arange(0.05, 0.96, 0.01):
        t = round(float(threshold), 2)
        y_pred = np.where(p_asleep >= t, 0, 1)
        m = binary_metrics(y_true, y_pred)
        m["threshold"] = t
        sweep.append(m)

    eligible = [m for m in sweep if m["precision_asleep"] >= 0.90 and m["recall_asleep"] >= 0.40]
    chosen = max(eligible, key=lambda m: (m["f1_asleep"], m["recall_asleep"])) if eligible else max(
        sweep, key=lambda m: m["f1_asleep"]
    )
    default_threshold = 0.5
    default_metrics = next(m for m in sweep if m["threshold"] == default_threshold)

    payload = {
        "model": str(best),
        "classes": names,
        "validation_count": int(len(y_true)),
        "chosen_threshold": chosen["threshold"],
        "chosen_metrics": chosen,
        "default_0_5_metrics": default_metrics,
        "max_precision": max(sweep, key=lambda m: m["precision_asleep"]),
        "sweep": sweep,
    }
    (OUT / "threshold.json").write_text(
        json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    report = [
        "# SleepWatch 睡/醒分类模型训练结果",
        "",
        f"- 最佳权重：`{best}`",
        f"- 设备：{torch.cuda.get_device_name(0) if device == 0 else 'CPU'}",
        f"- 验证集：{len(y_true)} 张（ asleep {int(np.sum(y_true == 0))} / awake {int(np.sum(y_true == 1))} ）",
        "",
        "## Ultralytics 验证指标",
        "",
        "```json",
        json.dumps(val_summary, ensure_ascii=False, indent=2),
        "```",
        "",
        "## 睡着类别阈值调优",
        "",
        f"- 默认阈值 0.50：准确率 {default_metrics['accuracy']:.3f}，精确率 {default_metrics['precision_asleep']:.3f}，召回率 {default_metrics['recall_asleep']:.3f}，F1 {default_metrics['f1_asleep']:.3f}",
        f"- 选定阈值 **{chosen['threshold']:.2f}**：准确率 {chosen['accuracy']:.3f}，精确率 {chosen['precision_asleep']:.3f}，召回率 {chosen['recall_asleep']:.3f}，F1 {chosen['f1_asleep']:.3f}",
        f"- 混淆矩阵（睡着为正类）：TP={chosen['tp']} FP={chosen['fp']} FN={chosen['fn']} TN={chosen['tn']}",
        "",
        "选定阈值的策略：在精确率 ≥ 0.90、召回率 ≥ 0.40 的候选中取 F1 最大者；若没有满足条件的候选，则取全局 F1 最大者。",
        "",
        "> 注意：验证集睡着样本只有 7 张，指标波动很大。这个模型适合作为“确认沉睡”阶段的二次校验，降低误报，不能直接替代原有的 YOLOv8n-pose 姿态关键点模型。",
        "",
    ]
    (OUT / "training_report.md").write_text("\n".join(report), encoding="utf-8")

    print("训练完成。摘要：")
    print(json.dumps({"chosen": chosen, "default": default_metrics}, ensure_ascii=False, indent=2))

    try:
        export_path = model.export(format="onnx", imgsz=224, dynamic=True)
        print("ONNX exported:", export_path)
    except Exception as exc:  # noqa: BLE001
        print("ONNX export skipped:", type(exc).__name__, exc)


if __name__ == "__main__":
    main()
