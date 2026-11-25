"""Prepare a YOLO-cls dataset from SleepWatch evidence snapshots.

Reads only from C:/Users/Administrator/Pictures/{确认睡觉,确认未睡觉},
strips the 46px black caption bar, and writes a stratified 80/20 split
into the workspace. Original photos are never modified.
"""

from __future__ import annotations

import os
import random
import shutil
from pathlib import Path

from PIL import Image

SOURCE = Path(r"C:\Users\Administrator\Pictures")
DEST = Path(__file__).resolve().parent / "datasets" / "sleepwatch_cls"
CLASSES = {"确认睡觉": "asleep", "确认未睡觉": "awake"}
CAPTION_HEIGHT = 46
SEED = 42
VAL_RATIO = 0.2


def collect() -> dict[str, list[Path]]:
    out: dict[str, list[Path]] = {}
    for folder, label in CLASSES.items():
        root = SOURCE / folder
        files = sorted(
            p for p in root.rglob("*")
            if p.suffix.lower() in {".jpg", ".jpeg", ".png"}
        )
        out[label] = files
    return out


def main() -> None:
    rng = random.Random(SEED)
    images = collect()
    print("源图片数量:", {k: len(v) for k, v in images.items()})

    if DEST.exists():
        shutil.rmtree(DEST)
    for split in ("train", "val"):
        for label in CLASSES.values():
            (DEST / split / label).mkdir(parents=True, exist_ok=True)

    manifest: list[str] = [
        "# SleepWatch 睡/醒二分类数据集",
        "",
        f"- 来源：`{SOURCE}` 下的 `确认睡觉` / `确认未睡觉` 两个文件夹（只读）",
        f"- 预处理：统一裁掉顶部 {CAPTION_HEIGHT}px 黑底文字条（课程/编号/时间/置信度）",
        f"- 划分：随机种子 {SEED}，每类独立按 {1 - VAL_RATIO:.0%}/{VAL_RATIO:.0%} 分层为 train/val",
        "- 分类：asleep = 确认睡觉，awake = 确认未睡觉",
        "",
    ]

    for label, files in images.items():
        shuffled = list(files)
        rng.shuffle(shuffled)
        n_val = max(1, round(len(shuffled) * VAL_RATIO))
        splits = {"val": shuffled[:n_val], "train": shuffled[n_val:]}
        manifest.append(f"## {label}")
        for split, chosen in splits.items():
            target = DEST / split / label
            kept = []
            for src in chosen:
                with Image.open(src) as im:
                    im = im.convert("RGB")
                    if im.height <= CAPTION_HEIGHT + 40:
                        continue
                    cropped = im.crop((0, CAPTION_HEIGHT, im.width, im.height))
                    dst = target / src.name
                    cropped.save(dst, "JPEG", quality=95, subsampling=0)
                    kept.append(dst.name)
            manifest.append(f"- {split}: {len(kept)} 张")
        manifest.append("")

    (DEST / "README.md").write_text("\n".join(manifest), encoding="utf-8")
    print("数据集已写入:", DEST)
    for split in ("train", "val"):
        counts = {label: len(list((DEST / split / label).glob("*.jpg"))) for label in CLASSES.values()}
        print(f"  {split}: {counts}")


if __name__ == "__main__":
    main()
