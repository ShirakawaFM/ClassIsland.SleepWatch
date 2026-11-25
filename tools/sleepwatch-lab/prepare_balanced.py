"""Create a class-balanced variant of the SleepWatch classification dataset.

Oversamples the rare "asleep" class with light augmentations and subsamples
the "awake" class. Validation split is copied unchanged for fair comparison.
"""

from __future__ import annotations

import random
import shutil
from pathlib import Path

from PIL import Image, ImageEnhance

BASE = Path(__file__).resolve().parent / "datasets" / "sleepwatch_cls"
DEST = BASE.parent / "sleepwatch_cls_balanced"
SEED = 42
TARGET_PER_CLASS = 208
AUG_VARIANTS = 7


def augment(im: Image.Image, kind: int) -> Image.Image:
    if kind == 0:
        return im
    if kind == 1:
        return im.transpose(Image.Transpose.FLIP_LEFT_RIGHT)
    if kind == 2:
        return ImageEnhance.Brightness(im).enhance(1.12)
    if kind == 3:
        return ImageEnhance.Brightness(im).enhance(0.88)
    if kind == 4:
        return ImageEnhance.Contrast(im).enhance(1.15)
    if kind == 5:
        return im.rotate(4, resample=Image.Resampling.BICUBIC, fillcolor=(30, 30, 30))
    return im.rotate(-4, resample=Image.Resampling.BICUBIC, fillcolor=(30, 30, 30))


def main() -> None:
    rng = random.Random(SEED)
    if DEST.exists():
        shutil.rmtree(DEST)
    for split in ("train", "val"):
        for label in ("asleep", "awake"):
            (DEST / split / label).mkdir(parents=True, exist_ok=True)

    # Validation unchanged.
    for label in ("asleep", "awake"):
        for src in (BASE / "val" / label).glob("*.jpg"):
            shutil.copy2(src, DEST / "val" / label / src.name)

    # Rare class: original + 7 light augmentations each.
    asleep_sources = sorted((BASE / "train" / "asleep").glob("*.jpg"))
    for src in asleep_sources:
        with Image.open(src) as im:
            im = im.convert("RGB")
            for i in range(AUG_VARIANTS + 1):
                stem = src.stem
                if i:
                    stem = f"{stem}_aug{i}"
                augment(im, i).save(
                    DEST / "train" / "asleep" / f"{stem}.jpg",
                    "JPEG", quality=95, subsampling=0,
                )

    # Majority class: random subsample.
    awake_sources = sorted((BASE / "train" / "awake").glob("*.jpg"))
    rng.shuffle(awake_sources)
    for src in awake_sources[:TARGET_PER_CLASS]:
        shutil.copy2(src, DEST / "train" / "awake" / src.name)

    counts = {
        split: {label: len(list((DEST / split / label).glob("*.jpg"))) for label in ("asleep", "awake")}
        for split in ("train", "val")
    }
    (DEST / "README.md").write_text(
        "# 平衡版数据集\n\n"
        f"- 稀有类 asleep：{len(asleep_sources)} 张原图 × {AUG_VARIANTS + 1}（含原图）= {counts['train']['asleep']} 张\n"
        f"- 多数类 awake：从 319 张中随机抽 {counts['train']['awake']} 张\n"
        "- 验证集与原数据集完全一致，便于直接对比\n"
        "- 增强方式：水平翻转、亮度 ±12%、对比度 +15%、旋转 ±4°\n",
        encoding="utf-8",
    )
    print("平衡数据集已写入:", DEST)
    print(counts)


if __name__ == "__main__":
    main()
