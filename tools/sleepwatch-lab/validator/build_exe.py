"""把数据校准器打包成单文件 exe。

用法（在 validator/ 目录下，使用带 torch/ultralytics 的 Python）：

    python build_exe.py

产物：validator/dist/SleepWatch.Calibrator.exe

说明：
- 内嵌 templates/ 与 baseline/（基线 ONNX、PT 与阈值），首次运行自动释放到 exe 旁的 data/ 目录。
- 打包 torch + ultralytics 体积较大（约 300~500 MB），启动时解包需要十几秒，属正常现象。
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
NAME = "SleepWatch.Calibrator"


def main() -> int:
    for path in (HERE / "app.py", HERE / "templates" / "index.html", HERE / "baseline"):
        if not path.exists():
            print(f"缺少打包输入：{path}")
            return 1

    sep = ";"
    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--noconfirm", "--clean",
        "--onefile",
        "--console",
        "--name", NAME,
        "--distpath", str(HERE / "dist"),
        "--workpath", str(HERE / "build"),
        "--specpath", str(HERE / "build"),
        "--add-data", f"{HERE / 'templates'}{sep}templates",
        "--add-data", f"{HERE / 'baseline'}{sep}baseline",
        "--collect-submodules", "ultralytics",
        "--collect-data", "ultralytics",
        "--collect-data", "torch",
        "--collect-all", "torchvision",
        "--copy-metadata", "ultralytics",
        "--copy-metadata", "torch",
        "--copy-metadata", "torchvision",
        "--copy-metadata", "onnxruntime",
        "--copy-metadata", "onnx",
        "--copy-metadata", "numpy",
        "--copy-metadata", "pillow",
        "--hidden-import", "ultralytics",
        "--hidden-import", "ultralytics.models.yolo.classify",
        "--hidden-import", "torchvision",
        "--hidden-import", "torchvision._C",
        "--hidden-import", "torchvision.ops",
        "--hidden-import", "onnxruntime",
        "--hidden-import", "onnx",
        "--hidden-import", "PIL._tkinter_finder",
        "--hidden-import", "flask",
        "--hidden-import", "jinja2",
        "--exclude-module", "tkinter",
        "--exclude-module", "PyQt5",
        "--exclude-module", "PySide6",
        "--exclude-module", "IPython",
        "--exclude-module", "jupyter",
        "--exclude-module", "pytest",
        str(HERE / "app.py"),
    ]

    print("开始打包：", " ".join(cmd[:6]), "...")
    result = subprocess.run(cmd, cwd=str(HERE))
    if result.returncode != 0:
        print("打包失败")
        return result.returncode

    exe = HERE / "dist" / f"{NAME}.exe"
    if not exe.exists():
        print("未找到产物", exe)
        return 1
    print(f"打包完成：{exe}（{exe.stat().st_size / 1048576:.1f} MB）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
