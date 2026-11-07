using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Win32;

using OpenCvSharp;

namespace ClassIsland.SleepWatch.Services;

/// <summary>一个摄像头设备。</summary>
public sealed class CameraDevice
{
    /// <summary>OpenCV 的设备序号。</summary>
    public int Index { get; init; }

    public string Name { get; init; } = "";

    /// <summary>现在被别的程序占着，打不开。</summary>
    public bool Occupied { get; init; }

    public override string ToString()
        => $"{Name}（{Index}）{(Occupied ? " · 占用中" : "")}";
}

/// <summary>
/// 枚举本机摄像头。
///
/// 没有可靠的"列出所有摄像头"的托管接口，所以用两步凑：先从注册表拿设备友好名，
/// 再逐个序号试开一次 VideoCapture，能开的就是可用设备。试开会占几百毫秒到一秒，
/// 所以必须在后台线程跑（设置页那边是 Task.Run 起来的）。
///
/// 注意：正在被本插件使用的那个设备再试开会失败，这种情况不能直接判成"没有"，
/// 要按"使用中"保留在列表里。
/// </summary>
public static class CameraEnumerator
{
    /// <summary>最多试到几号。一般机器不会超过 4 个。</summary>
    private const int MaxProbe = 6;

    /// <summary>USB 摄像头的设备类 GUID。</summary>
    private const string CameraClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}";

    /// <summary>
    /// 扫一遍。<paramref name="currentSource"/> 是当前配置里的视频源，
    /// 如果是纯数字就当作"正在使用"，即使试开失败也留在列表里。
    /// </summary>
    public static List<CameraDevice> Scan(string? currentSource)
    {
        var names = ReadFriendlyNames();
        var current = int.TryParse(currentSource?.Trim(), out var idx) ? idx : -1;

        var list = new List<CameraDevice>();

        NativeBootstrap.Ensure();

        for (var i = 0; i < MaxProbe; i++)
        {
            var opened = TryOpen(i, out var occupied);

            if (!opened && !occupied) continue;

            var name = i < names.Count && !string.IsNullOrWhiteSpace(names[i])
                ? names[i]
                : $"摄像头 {i}";

            var inUse = i == current;

            list.Add(new CameraDevice
            {
                Index = i,
                Name = inUse ? name + "（当前）" : name,
                Occupied = occupied
            });
        }

        // 当前配置的网络流 / 或者编号超范围：至少让用户看到自己在用什么
        if (current < 0 && !string.IsNullOrWhiteSpace(currentSource) && list.Count == 0)
        {
            list.Add(new CameraDevice
            {
                Index = -1,
                Name = "当前：自定义地址 " + currentSource,
                Occupied = false
            });
        }

        return list;
    }

    private static bool TryOpen(int index, out bool occupied)
    {
        occupied = false;

        try
        {
            using var capture = new VideoCapture();

            if (capture.Open(index, VideoCaptureAPIs.DSHOW) && capture.IsOpened())
            {
                // 真的读一帧，有些"打开成功"其实拿不到画面
                using var frame = new Mat();
                return capture.Read(frame) && !frame.Empty();
            }

            // 打不开有两种：没这个设备 / 被别的程序独占。用 ANY 再试一次区分。
            using var fallback = new VideoCapture();

            if (fallback.Open(index, VideoCaptureAPIs.ANY) && fallback.IsOpened())
            {
                using var frame = new Mat();
                return fallback.Read(frame) && !frame.Empty();
            }

            // ANY 也失败：这个序号上大概率有设备，只是被占了
            occupied = HasDeviceHint(index);
            return false;
        }
        catch
        {
            occupied = false;
            return false;
        }
    }

    /// <summary>注册表里有没有第 index 个设备（只能粗略对应，用来给"占用中"做个判据）。</summary>
    private static bool HasDeviceHint(int index)
    {
        try
        {
            var names = ReadFriendlyNames();
            return index < names.Count;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读设备友好名。读不到就返回空表，列表会退化成「摄像头 N」。</summary>
    private static List<string> ReadFriendlyNames()
    {
        var result = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CameraClassKey);
            if (key == null) return result;

            foreach (var sub in key.GetSubKeyNames().OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                using var dev = key.OpenSubKey(sub);
                var name = dev?.GetValue("FriendlyName") as string;

                if (!string.IsNullOrWhiteSpace(name)) result.Add(name!);
            }
        }
        catch
        {
            // 注册表读不到不影响主流程
        }

        return result;
    }

    /// <summary>给设置页显示用的一行摘要。</summary>
    public static string Describe(List<CameraDevice> devices)
    {
        if (devices.Count == 0) return "没找到可用摄像头";

        var occupied = devices.Count(d => d.Occupied);
        return occupied > 0
            ? $"找到 {devices.Count} 个设备，其中 {occupied} 个被占用"
            : $"找到 {devices.Count} 个设备";
    }
}
