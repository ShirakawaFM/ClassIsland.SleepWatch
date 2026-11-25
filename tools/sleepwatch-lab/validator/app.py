"""SleepWatch 模型实时升级系统。

三步闭环：
1. 上传图片 → 旧模型检测（/api/detect）
2. 人工修正确认标签（前端）
3. 确认入库并训练新模型（/api/commit + /api/train/status）

模型按版本热切换：后台训练完成后写入 current.json，下次检测自动加载新 ONNX。
"""

from __future__ import annotations

import base64
import io
import json
import os
import threading
import uuid
import webbrowser
from pathlib import Path

import numpy as np
import onnxruntime as ort
from flask import Flask, jsonify, render_template, request
from PIL import Image, ImageDraw

import upgrader
from paths import DATA_ROOT, FROZEN, TEMPLATE_DIR

MAX_FILES = 60
MAX_BYTES = 10 * 1024 * 1024
INPUT_SIZE = 224

app = Flask(__name__, template_folder=str(TEMPLATE_DIR))

# 打包成 exe 后 ultralytics 会误判依赖缺失并尝试联网安装，直接关掉它
os.environ.setdefault("YOLO_AUTOINSTALL", "false")
app.config["TEMPLATES_AUTO_RELOAD"] = True
_lock = threading.Lock()

upgrader.init()

_session: ort.InferenceSession | None = None
_input_name = ""
_output_name = ""
_threshold = 0.5
_asleep_idx = 0
_class_names: dict[int, str] = {}
_loaded_version = ""


def _ensure_model() -> None:
    """发现 current.json 版本变化后，原子切换 ONNX 推理会话。"""
    global _session, _input_name, _output_name, _threshold, _asleep_idx
    global _class_names, _loaded_version

    current = upgrader.load_current()
    if _session is not None and current["version"] == _loaded_version:
        return

    model_path = Path(current["onnx"])
    if not model_path.exists():
        model_path = Path(upgrader.BASELINE_ONNX)
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])

    names = {int(key): str(value) for key, value in current.get("classes", {}).items()}
    if not names:
        names = {0: "asleep", 1: "awake"}
    asleep_idx = next((i for i, name in names.items() if name == "asleep"), 0)

    with _lock:
        _session = session
        _input_name = session.get_inputs()[0].name
        _output_name = session.get_outputs()[0].name
        _threshold = float(current["threshold"])
        _asleep_idx = asleep_idx
        _class_names = names
        _loaded_version = current["version"]


def preprocess(image: Image.Image) -> np.ndarray:
    """与 Ultralytics classify_transforms 一致：短边缩放到 224，再中心裁剪。"""
    width, height = image.size
    scale = INPUT_SIZE / min(width, height)
    new_w = max(INPUT_SIZE, round(width * scale))
    new_h = max(INPUT_SIZE, round(height * scale))
    resized = image.resize((new_w, new_h), Image.Resampling.BILINEAR)
    left = (new_w - INPUT_SIZE) // 2
    top = (new_h - INPUT_SIZE) // 2
    cropped = resized.crop((left, top, left + INPUT_SIZE, top + INPUT_SIZE))
    arr = np.asarray(cropped, dtype=np.float32) / 255.0
    return arr.transpose(2, 0, 1)[None, ...]


def softmax(logits: np.ndarray) -> np.ndarray:
    shifted = logits - np.max(logits)
    exp = np.exp(shifted)
    return exp / np.sum(exp)


def infer(image: Image.Image) -> tuple[float, str, float]:
    """返回 p_asleep、预测类别、当前生效阈值。"""
    _ensure_model()
    arr = preprocess(image)
    with _lock:
        logits = _session.run([_output_name], {_input_name: arr})[0][0]
    probs = softmax(np.asarray(logits, dtype=np.float64))
    p_asleep = float(probs[_asleep_idx])
    pred = "asleep" if p_asleep >= _threshold else "awake"
    return p_asleep, pred, _threshold


def make_thumb(image: Image.Image, pred: str, correct: bool | None = None) -> str:
    thumb = image.copy()
    thumb.thumbnail((280, 190))
    draw = ImageDraw.Draw(thumb)
    if correct is None:
        color = (43, 232, 196) if pred == "asleep" else (96, 165, 250)
        label = pred
    elif pred == "asleep":
        color = (34, 197, 94) if correct else (239, 68, 68)
        label = f"{'OK' if correct else 'NG'} {pred}"
    else:
        color = (34, 197, 94) if correct else (249, 115, 22)
        label = f"{'OK' if correct else 'NG'} {pred}"

    draw.rectangle([0, 0, thumb.width - 1, thumb.height - 1], outline=color, width=3)
    draw.rectangle([4, 4, 4 + 9 * len(label) + 6, 22], fill=(15, 17, 21))
    draw.text((8, 7), label, fill=color)
    buffer = io.BytesIO()
    thumb.convert("RGB").save(buffer, "JPEG", quality=82)
    return "data:image/jpeg;base64," + base64.b64encode(buffer.getvalue()).decode("ascii")


def safe_ratio(num: int, den: int) -> float | None:
    return round(num / den, 4) if den else None


def compute_metrics(results: list[dict]) -> dict:
    tp = sum(1 for r in results if r["true_label"] == "asleep" and r["pred_label"] == "asleep")
    fp = sum(1 for r in results if r["true_label"] == "awake" and r["pred_label"] == "asleep")
    fn = sum(1 for r in results if r["true_label"] == "asleep" and r["pred_label"] == "awake")
    tn = sum(1 for r in results if r["true_label"] == "awake" and r["pred_label"] == "awake")
    precision = safe_ratio(tp, tp + fp)
    recall = safe_ratio(tp, tp + fn)
    f1 = (
        round(2 * precision * recall / (precision + recall), 4)
        if precision is not None and recall is not None and precision + recall
        else None
    )
    return {
        "total": len(results),
        "tp": tp, "fp": fp, "fn": fn, "tn": tn,
        "support_asleep": tp + fn,
        "support_awake": fp + tn,
        "fidelity_precision": precision,
        "accuracy": safe_ratio(tp + tn, len(results)),
        "recall": recall,
        "f1": f1,
        "false_positive_rate": safe_ratio(fp, fp + tn),
        "miss_rate": safe_ratio(fn, fn + tp),
    }


# --------------------------------------------------------------------------- 页面与状态

@app.get("/")
def index():
    return render_template("index.html")


@app.get("/api/info")
def info():
    _ensure_model()
    current = upgrader.load_current()
    train_ok, train_reason = upgrader.training_available()
    return jsonify({
        "version": current["version"],
        "model": current["onnx"],
        "threshold": _threshold,
        "classes": _class_names,
        "input_name": _input_name,
        "output_name": _output_name,
        "input_shape": [1, 3, INPUT_SIZE, INPUT_SIZE],
        "max_files": MAX_FILES,
        "dataset_counts": upgrader.dataset_counts(),
        "versions": len(upgrader.list_versions()),
        "training_busy": upgrader.training_busy(),
        "training_available": train_ok,
        "training_reason": train_reason,
        "data_root": str(DATA_ROOT),
    })


@app.get("/api/versions")
def versions():
    return jsonify({
        "current": upgrader.load_current()["version"],
        "versions": upgrader.list_versions(),
    })


@app.get("/api/train/status")
def train_status():
    job = upgrader.get_job(request.args.get("job_id", ""))
    if job is None:
        return jsonify({"error": "训练任务不存在"}), 404
    return jsonify(job)


# --------------------------------------------------------------------------- 步骤 1：检测

@app.post("/api/detect")
def detect():
    files = request.files.getlist("files")
    if not files:
        return jsonify({"error": "请至少上传一张图片"}), 400
    if len(files) > MAX_FILES:
        return jsonify({"error": f"一次最多处理 {MAX_FILES} 张图片"}), 400

    batch_id = upgrader.new_batch()
    results = []
    for item in files:
        raw = item.read()
        if len(raw) > MAX_BYTES:
            return jsonify({"error": f"图片过大：{item.filename}"}), 400
        try:
            original = Image.open(io.BytesIO(raw)).convert("RGB")
        except Exception:  # noqa: BLE001
            return jsonify({"error": f"无法解码图片：{item.filename}"}), 400

        image = upgrader.strip_caption(original)
        p_asleep, pred, threshold = infer(image)
        item_id = uuid.uuid4().hex[:10]
        ext = Path(item.filename or "").suffix.lower()
        if ext not in {".jpg", ".jpeg", ".png", ".webp", ".bmp"}:
            ext = ".jpg"
        upgrader.save_pending(batch_id, item_id, raw, ext)

        results.append({
            "id": item_id,
            "filename": item.filename or f"image_{item_id}{ext}",
            "pred_label": pred,
            "confidence": round(p_asleep if pred == "asleep" else 1.0 - p_asleep, 4),
            "p_asleep": round(p_asleep, 4),
            "p_awake": round(1.0 - p_asleep, 4),
            "region": {
                "x": 0, "y": 0, "w": image.width, "h": image.height,
                "note": "分类模型不输出目标框，识别区域为整张输入图",
            },
            "image_size": {"w": image.width, "h": image.height},
            "thumb": make_thumb(image, pred),
        })

    _ensure_model()
    return jsonify({
        "batch_id": batch_id,
        "model_version": _loaded_version,
        "threshold": threshold,
        "results": results,
    })


# --------------------------------------------------------------------------- 步骤 3：确认入库并训练

@app.post("/api/commit")
def commit():
    if upgrader.training_busy():
        return jsonify({"error": "已有训练任务进行中，请等待完成后再提交"}), 409

    payload = request.get_json(silent=True) or {}
    batch_id = payload.get("batch_id")
    items = payload.get("items", [])
    if not batch_id or not isinstance(items, list) or not items:
        return jsonify({"error": "缺少批次或确认结果"}), 400
    for item in items:
        if item.get("final_label") not in ("asleep", "awake"):
            return jsonify({"error": f"非法标签：{item.get('final_label')}"}), 400

    try:
        commit_info = upgrader.commit_batch(batch_id, items)
    except ValueError as exc:
        return jsonify({"error": str(exc)}), 400

    try:
        job_id = upgrader.start_training(batch_id, commit_info)
    except RuntimeError as exc:
        return jsonify({"error": str(exc)}), 409

    return jsonify({"job_id": job_id, "batch_id": batch_id, **commit_info})


# --------------------------------------------------------------------------- 兼容旧验证接口

@app.post("/api/validate")
def validate():
    files = request.files.getlist("files")
    labels = request.form.getlist("labels")
    if not files:
        return jsonify({"error": "请至少上传一张图片"}), 400
    if len(files) > MAX_FILES:
        return jsonify({"error": f"一次最多上传 {MAX_FILES} 张图片"}), 400
    if not labels or len(labels) != len(files):
        return jsonify({"error": "缺少图片标签"}), 400
    for label in labels:
        if label not in ("asleep", "awake"):
            return jsonify({"error": f"非法标签：{label}"}), 400

    results = []
    for item, label in zip(files, labels):
        raw = item.read()
        if len(raw) > MAX_BYTES:
            return jsonify({"error": f"图片过大：{item.filename}"}), 400
        try:
            image = upgrader.strip_caption(Image.open(io.BytesIO(raw)).convert("RGB"))
        except Exception:  # noqa: BLE001
            return jsonify({"error": f"无法解码图片：{item.filename}"}), 400

        p_asleep, pred, _ = infer(image)
        correct = pred == label
        results.append({
            "filename": item.filename,
            "true_label": label,
            "pred_label": pred,
            "confidence": round(p_asleep if pred == "asleep" else 1.0 - p_asleep, 4),
            "p_asleep": round(p_asleep, 4),
            "p_awake": round(1.0 - p_asleep, 4),
            "correct": correct,
            "region": {"x": 0, "y": 0, "w": image.width, "h": image.height},
            "image_size": {"w": image.width, "h": image.height},
            "thumb": make_thumb(image, pred, correct),
        })

    return jsonify({
        "model": str(upgrader.load_current()["onnx"]),
        "version": upgrader.load_current()["version"],
        "threshold": _threshold,
        "results": results,
        "metrics": compute_metrics(results),
    })


if __name__ == "__main__":
    port = int(os.environ.get("SLEEPWATCH_PORT", "8765"))
    url = f"http://127.0.0.1:{port}/"
    ok, reason = upgrader.training_available()
    print("=" * 62)
    print(" SleepWatch 数据校准器")
    print(f" 地址：{url}")
    print(f" 数据目录：{DATA_ROOT}")
    print(f" 训练能力：{'可用' if ok else '不可用 - ' + reason}")
    if FROZEN:
        print(" 关闭本窗口即可停止服务")
    print("=" * 62, flush=True)
    if os.environ.get("SLEEPWATCH_NO_BROWSER") != "1":
        threading.Timer(1.2, lambda: webbrowser.open(url)).start()
    app.run(host="127.0.0.1", port=port, debug=False, threaded=True)
