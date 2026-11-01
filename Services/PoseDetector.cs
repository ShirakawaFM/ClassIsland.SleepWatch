using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using OpenCvSharp;

namespace ClassIsland.SleepWatch.Services;

/// <summary>一个关键点：坐标 + 置信度。</summary>
public struct KeyPoint
{
    public float X;
    public float Y;
    public float Score;
}

/// <summary>检测到的一个人（不区分身份，只区分这一帧的位置和姿态）。</summary>
public sealed class PosePerson
{
    public Rect Box { get; init; }
    public float Score { get; init; }

    /// <summary>COCO 17 点：0 鼻 1 左眼 2 右眼 3 左耳 4 右耳 5 左肩 6 右肩 ……</summary>
    public KeyPoint[] Kpts { get; init; } = Array.Empty<KeyPoint>();

    /// <summary>头部相对肩线的下沉程度。<0 表示头在肩上，接近 0 或正数说明头低下去了。</summary>
    public float HeadDrop { get; set; } = float.NaN;

    /// <summary>是否判定为低头 / 伏案。</summary>
    public bool HeadDown { get; set; }

    /// <summary>姿态是否足以判断低头。背对镜头、只拍到后脑勺时给 false，不能瞎猜。</summary>
    public bool HeadJudgeable { get; set; }

    /// <summary>眼部是否清晰到能判断睁闭。不清楚时不能拿"闭眼"当证据。</summary>
    public bool EyesJudgeable { get; set; }

    public bool EyesOpen { get; set; }

    public double EyeScore { get; set; }

    public Point Center => new(Box.X + Box.Width / 2, Box.Y + Box.Height / 2);
}

/// <summary>
/// YOLOv8n-pose 人体姿态检测（OpenCV DNN 直接跑 ONNX，不需要额外的推理框架）。
///
/// 用人体的原因很直接：学生把头埋到桌上时，画面里根本没有脸，
/// 只有一个人形轮廓。人脸检测在这种状态下会整片漏掉，
/// 而姿态模型能从肩线判断"头掉下去了"。
/// </summary>
public sealed class PoseDetector : IDisposable
{
    private const int InputSize = 640;
    private const int Attrib = 56; // 4 框 + 1 置信度 + 17×3 关键点

    /// <summary>关键点置信度下限。低于这个值就当这个点没测出来。</summary>
    private const float ConfOk = 0.25f;

    /// <summary>眼部长边的目标尺寸。裁一块眼睛放大到这个大小再数暗像素。</summary>
    private const int EyeSampleWidth = 24;
    private const int EyeSampleHeight = 16;

    private OpenCvSharp.Dnn.Net? _net;

    // 推理输出一帧就是 56×8400 个 float，按每秒 4 帧算，每帧新开一个 1.9MB 的数组
    // 一年能把小对象堆推上去几十个 G —— 复用同一块，读之前只覆盖有效长度
    private float[] _decode = Array.Empty<float>();

    // 眼部评分：一帧里有多少人就有多少人×2 次 ROI 缩放，临时 Mat 也复用
    private Mat? _gray;
    private Mat? _eye;
    private readonly byte[] _eyePixels = new byte[EyeSampleWidth * EyeSampleHeight];

    public bool IsReady => _net != null;
    public string LastError { get; private set; } = "";

    public bool Load(string modelPath)
    {
        try
        {
            if (!File.Exists(modelPath))
            {
                LastError = "姿态模型不存在：" + modelPath;
                return false;
            }

            _net?.Dispose();
            _net = OpenCvSharp.Dnn.CvDnn.ReadNetFromOnnx(modelPath);
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            _net = null;
            LastError = "加载姿态模型失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 检测一帧里所有人的位置和姿态。返回坐标已经映射回传入图像的像素坐标。
    /// </summary>
    public List<PosePerson> Detect(Mat bgr, float minScore, double eyeThreshold)
    {
        var result = new List<PosePerson>();
        if (_net == null || bgr.Empty()) return result;

        try
        {
            // 等比缩放 + 灰边填充，避免把人拉变形
            var scale = Math.Min(InputSize / (float)bgr.Width, InputSize / (float)bgr.Height);
            var nw = Math.Max(1, (int)Math.Round(bgr.Width * scale));
            var nh = Math.Max(1, (int)Math.Round(bgr.Height * scale));
            var padX = (InputSize - nw) / 2;
            var padY = (InputSize - nh) / 2;

            using var resized = new Mat();
            Cv2.Resize(bgr, resized, new Size(nw, nh), 0, 0, InterpolationFlags.Linear);

            using var input = new Mat(new Size(InputSize, InputSize), MatType.CV_8UC3,
                new Scalar(114, 114, 114));

            using (var roi = new Mat(input, new Rect(padX, padY, nw, nh)))
            {
                resized.CopyTo(roi);
            }

            using var blob = OpenCvSharp.Dnn.CvDnn.BlobFromImage(
                input, 1.0 / 255.0, new Size(InputSize, InputSize),
                new Scalar(0, 0, 0), true, false);

            _net.SetInput(blob);
            using var output = _net.Forward();

            if (output.Empty() || output.Dims != 3) return result;

            var d1 = output.Size(1);
            var d2 = output.Size(2);

            // 有些导出是 [1,56,8400]，有些是 [1,8400,56]，两种都认
            var transposed = d1 != Attrib;
            var anchors = transposed ? d1 : d2;
            if (anchors <= 0) return result;

            var total = (long)Attrib * anchors;
            if (total <= 0 || total > 64_000_000) return result;

            if (_decode.Length < total) _decode = new float[total];
            var data = _decode;

            // 输出几乎总是连续的。万一不是，才复制一份出来读 —— 但复制完就扔，
            // 不再让 Mat 的 Clone 每帧多留一块非托管内存
            var flat = output.IsContinuous() ? null : output.Clone();

            try
            {
                Marshal.Copy(flat is null ? output.Data : flat.Data, data, 0, (int)total);
            }
            finally
            {
                flat?.Dispose();
            }

            var candidates = new List<PosePerson>();

            for (var j = 0; j < anchors; j++)
            {
                var score = Read(data, j, 4, transposed, anchors);
                if (score < minScore) continue;

                var cx = Read(data, j, 0, transposed, anchors);
                var cy = Read(data, j, 1, transposed, anchors);
                var w = Read(data, j, 2, transposed, anchors);
                var h = Read(data, j, 3, transposed, anchors);

                if (w < 4 || h < 4) continue;

                var box = new Rect(
                    (int)((cx - w / 2f - padX) / scale),
                    (int)((cy - h / 2f - padY) / scale),
                    (int)(w / scale),
                    (int)(h / scale));

                var kpts = new KeyPoint[17];
                for (var k = 0; k < 17; k++)
                {
                    var kx = Read(data, j, 5 + k * 3, transposed, anchors);
                    var ky = Read(data, j, 6 + k * 3, transposed, anchors);
                    var kc = Read(data, j, 7 + k * 3, transposed, anchors);

                    kpts[k] = new KeyPoint
                    {
                        X = (kx - padX) / scale,
                        Y = (ky - padY) / scale,
                        Score = kc
                    };
                }

                candidates.Add(new PosePerson { Box = box, Score = score, Kpts = kpts });
            }

            foreach (var person in Nms(candidates, 0.45f))
            {
                EvaluateHead(person);
                result.Add(person);
            }

            if (result.Count > 0 && result.Exists(CanJudgeEyes))
            {
                _gray ??= new Mat();
                Cv2.CvtColor(bgr, _gray, ColorConversionCodes.BGR2GRAY);

                foreach (var person in result)
                {
                    EvaluateEyes(_gray, person, eyeThreshold);
                }
            }
        }
        catch (Exception ex)
        {
            LastError = "姿态推理失败：" + ex.Message;
        }

        return result;
    }

    private static float Read(float[] data, int anchor, int attrib, bool transposed, int anchors)
    {
        var idx = transposed ? anchor * Attrib + attrib : attrib * anchors + anchor;
        return idx >= 0 && idx < data.Length ? data[idx] : 0f;
    }

    /// <summary>
    /// 头低没低，用"头相对肩线的落差 ÷ 肩宽"来判断，跟人的远近无关。
    /// 差值小于阈值（头掉到肩膀附近甚至更低）就算伏案。
    /// </summary>
    private static void EvaluateHead(PosePerson person)
    {
        var k = person.Kpts;
        if (k.Length < 17) return;

        var hasShoulders = k[5].Score > ConfOk && k[6].Score > ConfOk;
        var shoulderW = hasShoulders ? Math.Abs(k[6].X - k[5].X) : 0f;

        // 头部参考点：优先鼻子，其次眼睛，再退到耳朵
        float headY = float.NaN;
        if (k[0].Score > ConfOk) headY = k[0].Y;
        else if (k[1].Score > ConfOk && k[2].Score > ConfOk) headY = (k[1].Y + k[2].Y) / 2f;
        else if (k[3].Score > ConfOk) headY = k[3].Y;
        else if (k[4].Score > ConfOk) headY = k[4].Y;

        if (hasShoulders && shoulderW > 8 && !float.IsNaN(headY))
        {
            var shoulderMidY = (k[5].Y + k[6].Y) / 2f;
            var drop = (headY - shoulderMidY) / shoulderW;

            person.HeadDrop = drop;
            person.HeadDown = drop > -0.12f;
            person.HeadJudgeable = true;
            return;
        }

        // 肩线看不见时，退回到人体框的宽高比。但前提是至少能看到头部的某些点；
        // 完全看不到（背对镜头、只拍到后脑勺）时不能下结论，否则会把"背对"误判成"低头"。
        var anyHeadVisible =
            k[0].Score > ConfOk || k[1].Score > ConfOk || k[2].Score > ConfOk ||
            k[3].Score > ConfOk || k[4].Score > ConfOk;

        if (anyHeadVisible)
        {
            var w = Math.Max(1, person.Box.Width);
            var h = Math.Max(1, person.Box.Height);
            var ratio = h / (float)w;

            person.HeadDown = ratio < 1.25f;
            person.HeadJudgeable = true;
        }
        else
        {
            person.HeadDown = false;
            person.HeadJudgeable = false;
        }
    }

    /// <summary>
    /// 这张脸值不值得去数眼白：框太小、眼睛关键点没出来，看了也是噪声。
    /// 和 <see cref="EvaluateEyes"/> 开头的两道闸门是同一套判断 —— 这里先过一遍，
    /// 免得整帧转灰度转了却一个人都用不上。
    /// </summary>
    private static bool CanJudgeEyes(PosePerson person)
    {
        var k = person.Kpts;
        if (k.Length < 17) return false;
        if (person.Box.Width < 26) return false;

        return k[1].Score > ConfOk || k[2].Score > ConfOk;
    }

    /// <summary>
    /// 睁眼判定：拿姿态模型给的眼睛关键点，裁一小块算"暗像素占比"。
    /// 睁眼时虹膜是一块明显暗区，闭眼时基本是均匀肤色。
    /// 看不清（脸太小、被挡、过曝）就标记为不可判，不能当作闭眼。
    /// </summary>
    private void EvaluateEyes(Mat gray, PosePerson person, double threshold)
    {
        var k = person.Kpts;
        if (k.Length < 17) return;

        var hasLeft = k[1].Score > ConfOk;
        var hasRight = k[2].Score > ConfOk;

        if (!hasLeft && !hasRight) return;

        // 脸太小，眼部只有几个像素，测出来的全是噪声
        if (person.Box.Width < 26) return;

        // 用双眼间距当尺度基准，比用脸框宽度稳
        float scaleRef;
        if (hasLeft && hasRight)
        {
            var dx = k[2].X - k[1].X;
            var dy = k[2].Y - k[1].Y;
            scaleRef = (float)Math.Sqrt(dx * dx + dy * dy);
        }
        else if (k[3].Score > ConfOk && k[4].Score > ConfOk)
        {
            scaleRef = Math.Abs(k[4].X - k[3].X) * 0.45f;
        }
        else
        {
            scaleRef = person.Box.Width * 0.2f;
        }

        if (scaleRef < 4) return;

        var radius = Math.Max(2, (int)(scaleRef * 0.55));

        var best = -1.0;
        if (hasLeft) best = Math.Max(best, EyeOpenScore(gray, k[1].X, k[1].Y, radius));
        if (hasRight) best = Math.Max(best, EyeOpenScore(gray, k[2].X, k[2].Y, radius));

        if (best < 0) return;

        person.EyeScore = best;
        person.EyesJudgeable = true;
        person.EyesOpen = best > threshold;
    }

    private double EyeOpenScore(Mat gray, float eyeX, float eyeY, int radius)
    {
        try
        {
            var x = (int)eyeX - radius;
            var y = (int)eyeY - radius;
            var w = radius * 2;
            var h = radius * 2;

            x = Math.Clamp(x, 0, Math.Max(0, gray.Width - 1));
            y = Math.Clamp(y, 0, Math.Max(0, gray.Height - 1));
            w = Math.Min(w, gray.Width - x);
            h = Math.Min(h, gray.Height - y);

            if (w < 3 || h < 3) return -1;

            using var roi = new Mat(gray, new Rect(x, y, w, h));

            _eye ??= new Mat();
            Cv2.Resize(roi, _eye, new Size(EyeSampleWidth, EyeSampleHeight), 0, 0, InterpolationFlags.Area);

            var total = _eye.Rows * _eye.Cols;
            if (total <= 0 || !_eye.IsContinuous()) return -1;
            if (total > _eyePixels.Length) return -1;

            var buffer = _eyePixels;
            Marshal.Copy(_eye.Data, buffer, 0, total);

            double sum = 0;
            for (var i = 0; i < total; i++) sum += buffer[i];
            var mean = sum / total;

            double sq = 0;
            for (var i = 0; i < total; i++)
            {
                var d = buffer[i] - mean;
                sq += d * d;
            }

            var std = Math.Sqrt(sq / total);

            // 区域太平（头发挡住 / 过曝），无法判断
            if (std < 4.0) return -1;

            var cut = mean - 0.85 * std;
            var dark = 0;
            for (var i = 0; i < total; i++)
            {
                if (buffer[i] < cut) dark++;
            }

            return dark / (double)total;
        }
        catch
        {
            return -1;
        }
    }

    private static List<PosePerson> Nms(List<PosePerson> items, float iouThreshold)
    {
        var sorted = new List<PosePerson>(items);
        sorted.Sort((a, b) => b.Score.CompareTo(a.Score));

        var kept = new List<PosePerson>();
        var suppressed = new bool[sorted.Count];

        for (var i = 0; i < sorted.Count; i++)
        {
            if (suppressed[i]) continue;
            kept.Add(sorted[i]);

            for (var j = i + 1; j < sorted.Count; j++)
            {
                if (suppressed[j]) continue;
                if (Iou(sorted[i].Box, sorted[j].Box) > iouThreshold) suppressed[j] = true;
            }
        }

        return kept;
    }

    private static float Iou(Rect a, Rect b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        var w = x2 - x1;
        var h = y2 - y1;
        if (w <= 0 || h <= 0) return 0f;

        var inter = w * h;
        var union = a.Width * (float)a.Height + b.Width * (float)b.Height - inter;
        return union <= 0 ? 0f : inter / union;
    }

    public void Dispose()
    {
        _net?.Dispose();
        _net = null;

        _gray?.Dispose();
        _gray = null;

        _eye?.Dispose();
        _eye = null;

        _decode = Array.Empty<float>();
    }
}
