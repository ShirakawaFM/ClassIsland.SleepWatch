"""统一的路径解析：同时支持源码运行与 PyInstaller 打包后的 exe 运行。

源码运行：
    <工作区>/validator/paths.py          → WORKSPACE = <工作区>
    <工作区>/training/deliverables/...   → 基线权重

exe 运行：
    <exe 所在目录>/SleepWatch.Calibrator.exe
    <exe 所在目录>/data/                 → 可写数据目录（数据集、版本、日志）
    exe 内嵌 baseline/                    → 基线权重与阈值（只读，运行时释放到临时目录）
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

FROZEN = bool(getattr(sys, "frozen", False))

# 只读资源根：源码模式是 validator/ 目录，exe 模式是 PyInstaller 的解包目录
BUNDLE = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))

if FROZEN:
    # exe 所在目录（不是临时解包目录），所有可写数据放这里，卸载/升级都不丢
    APP_DIR = Path(sys.executable).resolve().parent
    DATA_ROOT = Path(os.environ.get("SLEEPWATCH_DATA", APP_DIR / "data")).resolve()
    TEMPLATE_DIR = BUNDLE / "templates"
    BASELINE_DIR = BUNDLE / "baseline"
else:
    APP_DIR = Path(__file__).resolve().parent
    DATA_ROOT = Path(os.environ.get("SLEEPWATCH_DATA", APP_DIR.parent)).resolve()
    TEMPLATE_DIR = APP_DIR / "templates"
    BASELINE_DIR = APP_DIR.parent / "training" / "deliverables"

TRAINING = DATA_ROOT / "training"
DATASETS = DATA_ROOT / "datasets"
LIVE = TRAINING / "live"

BASELINE_ONNX = BASELINE_DIR / "sleepwatch_cls_balanced.onnx"
BASELINE_PT = BASELINE_DIR / "sleepwatch_cls_balanced.pt"
BASELINE_THRESHOLD = BASELINE_DIR / "threshold_balanced.json"
BASELINE_DATASET = DATASETS / "sleepwatch_cls_balanced"
