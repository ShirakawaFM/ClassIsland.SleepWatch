"""SleepWatch 模型实时升级：批次暂存、数据集累积、后台训练与版本管理。

目录结构（training/live）：
    pending/<batch_id>/     上传后待确认的图片
    dataset/{train,val}/    持续累积的 YOLO-cls 数据集
    versions/<version>/     每次升级后的权重、阈值与指标
    current.json            当前生效版本（服务重启后自动恢复）
    annotations.jsonl       每次人工确认的标注记录

新图片经过“旧模型检测 → 人工修正 → 确认入库”后，从当前生效权重续训，
导出 ONNX 并原子切换 current.json；Web 服务下次推理时自动加载新版本。
"""

from __future__ import annotations

import contextlib
import io
import json
import os
import random
import shutil
import threading
import time
import traceback
import uuid
from datetime import datetime
from pathlib import Path

from PIL import Image, ImageStat

from paths import (
    BASELINE_DATASET,
    BASELINE_ONNX,
    BASELINE_PT,
    BASELINE_THRESHOLD,
    DATA_ROOT,
    LIVE,
    TRAINING,
)

PENDING = LIVE / "pending"
DATASET = LIVE / "dataset"
VERSIONS = LIVE / "versions"
RUNS = TRAINING / "runs_live"
CURRENT_FILE = LIVE / "current.json"
ANNOTATIONS = LIVE / "annotations.jsonl"

EPOCHS = 15
BATCH = 16
CAPTION_HEIGHT = 46
CLASSES = ("asleep", "awake")

_jobs: dict[str, dict] = {}
_jobs_lock = threading.Lock()


# --------------------------------------------------------------------------- 初始化

def init() -> None:
    """创建目录、初始化基线版本；可重复调用。"""
    for path in (LIVE, PENDING, VERSIONS, RUNS):
        path.mkdir(parents=True, exist_ok=True)

    # 首次启动且带基线数据集时先整体复制，再补建缺失的空目录
    if not DATASET.exists() and BASELINE_DATASET.exists():
        shutil.copytree(BASELINE_DATASET, DATASET, dirs_exist_ok=True)
    for split in ("train", "val"):
        for label in CLASSES:
            (DATASET / split / label).mkdir(parents=True, exist_ok=True)

    if not CURRENT_FILE.exists():
        if not BASELINE_ONNX.exists():
            raise FileNotFoundError(
                f"缺少基线模型 {BASELINE_ONNX.name}，无法初始化校准器。"
            )
        vdir = VERSIONS / "v1_baseline"
        wdir = vdir / "weights"
        wdir.mkdir(parents=True, exist_ok=True)
        shutil.copy2(BASELINE_ONNX, wdir / "best.onnx")
        if BASELINE_PT.exists():
            shutil.copy2(BASELINE_PT, wdir / "best.pt")
        if BASELINE_THRESHOLD.exists():
            payload = json.loads(BASELINE_THRESHOLD.read_text(encoding="utf-8"))
        else:
            payload = {"chosen_threshold": 0.5, "classes": {"0": "asleep", "1": "awake"}}
        meta = {
            "version": "v1_baseline",
            "onnx": str(wdir / "best.onnx"),
            "pt": str(wdir / "best.pt") if (wdir / "best.pt").exists() else "",
            "threshold": float(payload["chosen_threshold"]),
            "classes": {int(k): str(v) for k, v in payload["classes"].items()},
            "created_at": datetime.now().isoformat(timespec="seconds"),
            "source": "baseline",
            "metrics": payload.get("chosen_metrics", {}),
            "dataset_counts": dataset_counts(),
        }
        (vdir / "meta.json").write_text(
            json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        write_current(meta)


def write_current(meta: dict) -> None:
    CURRENT_FILE.write_text(
        json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8"
    )


def load_current() -> dict:
    return json.loads(CURRENT_FILE.read_text(encoding="utf-8"))


def dataset_counts() -> dict:
    return {
        split: {
            label: len(list((DATASET / split / label).glob("*.jpg")))
            for label in CLASSES
        }
        for split in ("train", "val")
    }


def list_versions() -> list[dict]:
    versions = []
    for folder in VERSIONS.iterdir():
        meta_path = folder / "meta.json"
        if meta_path.is_file():
            versions.append(json.loads(meta_path.read_text(encoding="utf-8")))
    return sorted(versions, key=lambda m: m.get("created_at", ""), reverse=True)


# --------------------------------------------------------------------------- 批次暂存

def new_batch() -> str:
    batch_id = datetime.now().strftime("%Y%m%d_%H%M%S") + "_" + uuid.uuid4().hex[:6]
    (PENDING / batch_id).mkdir(parents=True, exist_ok=True)
    return batch_id


def save_pending(batch_id: str, item_id: str, raw: bytes, ext: str = ".jpg") -> Path:
    folder = PENDING / batch_id
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / f"{item_id}{ext}"
    path.write_bytes(raw)
    return path


def pending_path(batch_id: str, item_id: str) -> Path | None:
    folder = PENDING / batch_id
    if not folder.exists():
        return None
    hits = list(folder.glob(f"{item_id}.*"))
    return hits[0] if hits else None


# --------------------------------------------------------------------------- 人工确认入库

def strip_caption(image: Image.Image) -> Image.Image:
    """裁掉插件快照顶部的黑底文字条；普通图片保持不变。"""
    if image.height <= CAPTION_HEIGHT + 40:
        return image
    top = image.convert("L").crop((0, 0, image.width, CAPTION_HEIGHT))
    if ImageStat.Stat(top).mean[0] < 80:
        return image.crop((0, CAPTION_HEIGHT, image.width, image.height))
    return image


def commit_batch(batch_id: str, decisions: list[dict]) -> dict:
    """把人工确认后的图片写入累积数据集，并记录标注。"""
    if not (PENDING / batch_id).exists():
        raise ValueError("批次不存在或已过期，请重新检测")

    rng = random.Random(int(time.time()))
    added = {label: 0 for label in CLASSES}
    val_added = {label: 0 for label in CLASSES}
    records = []

    for decision in decisions:
        item_id = str(decision.get("id", ""))
        label = decision.get("final_label")
        if label not in CLASSES:
            continue
        src = pending_path(batch_id, item_id)
        if src is None:
            continue

        with Image.open(src) as raw:
            image = strip_caption(raw.convert("RGB"))
            split = "val" if rng.random() < 0.2 else "train"
            target_dir = DATASET / split / label
            target_dir.mkdir(parents=True, exist_ok=True)
            dst = target_dir / f"{batch_id}_{item_id}.jpg"
            image.save(dst, "JPEG", quality=95, subsampling=0)

        added[label] += 1
        if split == "val":
            val_added[label] += 1
        records.append({
            "batch_id": batch_id,
            "item_id": item_id,
            "final_label": label,
            "split": split,
            "file": str(dst),
            "committed_at": datetime.now().isoformat(timespec="seconds"),
        })

    if not records:
        raise ValueError("没有可入库的图片")

    with ANNOTATIONS.open("a", encoding="utf-8") as handle:
        for record in records:
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")

    _ensure_val_minimum()

    return {
        "added": added,
        "val_added": val_added,
        "total": len(records),
        "dataset_counts": dataset_counts(),
    }


def _ensure_val_minimum(per_label: int = 1) -> None:
    """YOLO-cls 要求验证集里每个类别都有样本。

    首次使用时数据集为空，20% 的随机划分可能让某一类在验证集里挂零，
    导致训练直接报错。这里从训练集借一张过去兜底。
    """
    for label in CLASSES:
        val_dir = DATASET / "val" / label
        train_dir = DATASET / "train" / label
        while len(list(val_dir.glob("*.jpg"))) < per_label:
            candidates = sorted(train_dir.glob("*.jpg"))
            if not candidates:
                break
            shutil.move(str(candidates[0]), str(val_dir / candidates[0].name))


# --------------------------------------------------------------------------- 训练任务

def training_busy() -> bool:
    with _jobs_lock:
        return any(job["status"] in ("queued", "running") for job in _jobs.values())


def training_available() -> tuple[bool, str]:
    """检测当前运行环境是否具备训练能力（exe 内已内置 ultralytics 时恒为可用）。"""
    try:
        import ultralytics  # noqa: F401
        import torch  # noqa: F401
    except Exception as exc:  # noqa: BLE001
        return False, f"缺少训练依赖：{exc}"
    return True, ""


def start_training(batch_id: str, commit_info: dict) -> str:
    if training_busy():
        raise RuntimeError("已有训练任务正在进行，请等待完成")

    ok, reason = training_available()
    if not ok:
        raise RuntimeError(reason)

    counts = dataset_counts()
    need = {"train": 2, "val": 1}
    for split, minimum in need.items():
        for label in CLASSES:
            if counts[split][label] < minimum:
                raise RuntimeError(
                    f"{split}/{label} 只有 {counts[split][label]} 张，至少需要 {minimum} 张才能训练；"
                    "请继续上传并确认更多图片"
                )

    base_version = load_current().get("version", "unknown")
    version = f"v{len(list_versions()) + 1}_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
    job_id = uuid.uuid4().hex[:12]
    job = {
        "job_id": job_id,
        "version": version,
        "base_version": base_version,
        "batch_id": batch_id,
        "status": "queued",
        "progress": 0,
        "logs": [],
        "commit": commit_info,
        "metrics": None,
        "error": None,
        "started_at": datetime.now().isoformat(timespec="seconds"),
        "finished_at": None,
    }
    with _jobs_lock:
        _jobs[job_id] = job

    threading.Thread(target=_train_worker, args=(job_id,), daemon=True).start()
    return job_id


def get_job(job_id: str) -> dict | None:
    with _jobs_lock:
        job = _jobs.get(job_id)
        if job is None:
            return None
        return {key: list(value) if isinstance(value, list) else value
                for key, value in job.items()}


def _append_log(job: dict, line: str) -> None:
    with _jobs_lock:
        job["logs"].append(line.rstrip())
        if len(job["logs"]) > 400:
            job["logs"] = job["logs"][-400:]


def _train_worker(job_id: str) -> None:
    job = _jobs[job_id]
    version = job["version"]
    try:
        job["status"] = "running"
        _append_log(job, f"开始训练 {version}（基于 {job['base_version']}）")
        _append_log(job, f"数据集：{dataset_counts()}")

        import numpy as np

        # 打包成 exe 后 importlib.metadata 看不到 onnxruntime 的包元数据，
        # ultralytics 会以为缺依赖并尝试联网 pip 安装，然后一直卡住。
        # 关掉自动安装：依赖已经随 exe 打包，这里只需要它别去联网。
        os.environ.setdefault("YOLO_AUTOINSTALL", "false")
        os.environ.setdefault("YOLO_VERBOSE", "false")
        from ultralytics import YOLO

        current = load_current()
        base_pt = Path(current["pt"])
        if not base_pt.exists():
            base_pt = BASELINE_PT
        model = YOLO(str(base_pt))
        _append_log(job, f"已加载权重：{base_pt.name}")

        def on_epoch_end(trainer):
            epoch = trainer.epoch + 1
            job["progress"] = min(60, int(epoch / EPOCHS * 60))
            _append_log(job, f"Epoch {epoch}/{EPOCHS} 完成")

        model.add_callback("on_fit_epoch_end", on_epoch_end)

        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer), contextlib.redirect_stderr(buffer):
            model.train(
                data=str(DATASET),
                epochs=EPOCHS,
                imgsz=224,
                batch=BATCH,
                device="cpu",
                patience=10,
                workers=0,
                project=str(RUNS),
                name=version,
                exist_ok=True,
                verbose=True,
                plots=False,
                val=True,
            )
        job["progress"] = 65
        _append_log(job, "训练结束，正在验证并搜索阈值…")
        for line in buffer.getvalue().splitlines()[-20:]:
            if line.strip():
                _append_log(job, line.strip())

        best_pt = RUNS / version / "weights" / "best.pt"
        if not best_pt.exists():
            raise FileNotFoundError(f"未找到训练权重：{best_pt}")
        model = YOLO(str(best_pt))

        val_root = DATASET / "val"
        asleep_files = sorted((val_root / "asleep").glob("*.jpg"))
        awake_files = sorted((val_root / "awake").glob("*.jpg"))
        paths = asleep_files + awake_files
        y_true = np.array([0] * len(asleep_files) + [1] * len(awake_files))
        names = model.names
        asleep_idx = next(
            i for i, name in names.items()
            if str(name).lower() in {"asleep", "sleep", "睡觉"}
        )

        with contextlib.redirect_stdout(io.StringIO()):
            predictions = model.predict(
                source=[str(path) for path in paths],
                imgsz=224, device="cpu", batch=32, verbose=False,
            )
        p_asleep = np.array([
            float(result.probs.data[asleep_idx].cpu()) for result in predictions
        ])

        sweep = []
        for raw_t in np.arange(0.05, 0.96, 0.01):
            threshold = round(float(raw_t), 2)
            y_pred = np.where(p_asleep >= threshold, 0, 1)
            tp = int(np.sum((y_true == 0) & (y_pred == 0)))
            fp = int(np.sum((y_true == 1) & (y_pred == 0)))
            fn = int(np.sum((y_true == 0) & (y_pred == 1)))
            tn = int(np.sum((y_true == 1) & (y_pred == 1)))
            precision = tp / (tp + fp) if tp + fp else 0.0
            recall = tp / (tp + fn) if tp + fn else 0.0
            f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
            sweep.append({
                "threshold": threshold,
                "tp": tp, "fp": fp, "fn": fn, "tn": tn,
                "precision_asleep": round(precision, 4),
                "recall_asleep": round(recall, 4),
                "f1_asleep": round(f1, 4),
                "accuracy": round((tp + tn) / len(y_true), 4) if len(y_true) else 0.0,
            })

        eligible = [
            item for item in sweep
            if item["precision_asleep"] >= 0.90 and item["recall_asleep"] >= 0.40
        ]

        # 验证集太小时，扫阈值等于在噪声上挑极值：往往会挑出 0.05 这种
        # “宁可全判睡着”的解，用户第一次上传十来张图就会得到满屏误报。
        # 样本不够就退回中性阈值 0.5，等数据攒厚了再让扫描决定。
        if len(y_true) < 12:
            chosen = min(sweep, key=lambda item: abs(item["threshold"] - 0.5))
            _append_log(
                job,
                f"验证集只有 {len(y_true)} 张，阈值扫描会退化，本次固定用 {chosen['threshold']}",
            )
        else:
            chosen = (
                max(eligible, key=lambda item: (item["f1_asleep"], item["recall_asleep"]))
                if eligible else max(sweep, key=lambda item: item["f1_asleep"])
            )
        job["progress"] = 80
        _append_log(
            job,
            f"阈值 {chosen['threshold']}：P={chosen['precision_asleep']} "
            f"R={chosen['recall_asleep']} F1={chosen['f1_asleep']}",
        )

        with contextlib.redirect_stdout(io.StringIO()):
            # simplify=False：onnxslim 不在打包列表里，留着它会让 export 去联网装依赖
            exported = Path(
                model.export(format="onnx", imgsz=224, dynamic=False, simplify=False)
            )
        _append_log(job, "ONNX 导出完成，正在写入版本…")

        version_dir = VERSIONS / version
        weights_dir = version_dir / "weights"
        weights_dir.mkdir(parents=True, exist_ok=True)
        shutil.copy2(best_pt, weights_dir / "best.pt")
        shutil.copy2(exported, weights_dir / "best.onnx")

        meta = {
            "version": version,
            "onnx": str(weights_dir / "best.onnx"),
            "pt": str(weights_dir / "best.pt"),
            "threshold": chosen["threshold"],
            "classes": {int(key): str(value) for key, value in names.items()},
            "created_at": datetime.now().isoformat(timespec="seconds"),
            "source": f"人工确认批次 {job['batch_id']}",
            "metrics": chosen,
            "dataset_counts": dataset_counts(),
            "added": job["commit"].get("added", {}),
            "base_version": job["base_version"],
            "threshold_sweep": sweep,
        }
        (version_dir / "meta.json").write_text(
            json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        write_current(meta)

        job["metrics"] = chosen
        job["progress"] = 100
        job["status"] = "done"
        job["finished_at"] = datetime.now().isoformat(timespec="seconds")
        _append_log(job, f"模型已升级到 {version}，下次检测自动生效")

    except Exception as exc:  # noqa: BLE001
        job["status"] = "failed"
        job["error"] = traceback.format_exc()
        job["finished_at"] = datetime.now().isoformat(timespec="seconds")
        _append_log(job, f"训练失败：{exc}")
