# SleepWatch 睡/醒分类实验工具

本目录是 SleepWatch 插件的**实验性**睡/醒二分类工具集，不是插件运行时依赖。插件正式推理仍然使用 `models/yolov8n-pose.onnx`（姿态关键点模型）。

> ⚠️ 定位：这里的分类器只能作为「确认沉睡」阶段的**二次校验**来降低误报，**不能替代**姿态模型。姿态模型负责找人、17 关键点、轨迹和计时；分类器只判断一张已经裁好的人像像不像睡着。

## 目录

```
tools/sleepwatch-lab/
├── README.md                 本说明
├── TRAINING_REPORT.md        训练报告（数据、两轮对比、阈值、ONNX 规格、接入建议）
├── prepare_dataset.py        从相册只读建立原始 YOLO-cls 数据集（裁掉顶部 46px 文字条）
├── prepare_balanced.py       稀有类过采样，生成平衡数据集
├── train_classifier.py       训练、验证、阈值扫描、导出 ONNX
├── thresholds/               两轮实验的精简阈值结果
└── validator/                数据校准器（本地 Web 闭环工具）
    ├── README.md
    ├── app.py                Flask API 与 ONNX 推理
    ├── upgrader.py           批次暂存、数据集累积、后台训练、版本热切换
    ├── paths.py              源码/exe 双模式路径解析
    ├── build_exe.py          打包单文件 exe
    └── templates/index.html  前端界面
```

## 依赖

Python 3.10+：

```bash
pip install torch torchvision ultralytics pillow onnx onnxruntime flask numpy
```

## 训练流程

在 `tools/sleepwatch-lab/` 目录下执行：

```bash
# 只读扫描相册，默认 %USERPROFILE%\Pictures\{确认睡觉,确认未睡觉}
# 可用环境变量 SLEEPWATCH_PICTURES 指定别的根目录
python prepare_dataset.py
python prepare_balanced.py
python train_classifier.py --data datasets/sleepwatch_cls_balanced --name sleepwatch_cls_balanced
```

产物在 `training/runs/<name>/weights/` 与 `training/results/<name>/`。
预训练权重 `yolov8n-cls.pt` 首次运行会自动下载，也可用 `SLEEPWATCH_PRETRAINED` 指向本地文件。

## 数据校准器

`validator/` 是一个本地 Web 闭环工具：

```
上传图片 → 当前模型检测 → 人工改标签 → 确认入库 → 后台续训 → 新模型热切换
```

### 直接用打包好的 exe

发布页的 `SleepWatch.Calibrator.v1.5.3.0.zip` 里已经带了 exe 与一键脚本，解压后双击 `一键启动数据校准器.cmd` 即可。

### 从源码运行

```bash
python validator/app.py
```

浏览器打开 <http://127.0.0.1:8765/>。首次运行需要把基线权重放到 `training/deliverables/`：

- `sleepwatch_cls_balanced.onnx`
- `sleepwatch_cls_balanced.pt`
- `threshold_balanced.json`

这三个是训练产物（二进制/大文件），未随源码提交，按上面的训练流程生成后放入，或从发布页的资产里下载。

### 重新打包 exe

```bash
python validator/build_exe.py
# 产物：validator/dist/SleepWatch.Calibrator.exe
```

打包后的数据都在 exe 同级的 `data/` 目录，替换 exe 不会丢数据；可用环境变量 `SLEEPWATCH_DATA` 改位置，`SLEEPWATCH_PORT` 改端口。

## ONNX 规格

| 项目 | 值 |
| --- | --- |
| 输入名 | `images` |
| 输入形状 | `[1, 3, 224, 224]` |
| 输入格式 | RGB，NCHW，像素值 0～1 |
| 输出名 | `output0` |
| 输出形状 | `[1, 2]`，未归一化 logits；softmax 后第 0 维为 `p_asleep` |
| 预处理 | 短边缩放到 224 → 中心裁剪 → RGB → /255 → HWC 转 CHW |
| 归一化 | 无需额外归一化 |

完整细节与接入建议见 `TRAINING_REPORT.md`。

## 隐私

数据集来自本人相册，**不随仓库提交**。分类器与校准器全程本机运行，不联网、不上传，也不认人。
