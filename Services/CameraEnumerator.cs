using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Win32;

namespace ClassIsland.SleepWatch.Services;

/// <summary>一个摄像头设备。</summary>
public sealed class CameraDevice
{
    /// <summary>OpenCV 的设备序号。</summary>
    public int Index { get; init; }

    public string Name { get; init; } = "";

    /// <summary>现在被别的程序占着，打不开。</summary>
    public bool Occupied { get; init; }

    /// <summary>试开成功，能出画面。</summary>
    public bool Usable { get; init; } = true;

    /// <summary>给下拉框看的补充说明，没有就不显示。</summary>
    public string Note { get; init; } = "";

    public override string ToString()
    {
        // Name 里已经带「（当前）」的就别再把序号叠一遍
        var head = Name.EndsWith("（当前）", StringComparison.Ordinal) ? Name : $"{Name}（{Index}）";
        return string.IsNullOrEmpty(Note) ? head : $"{head} · {Note}";
    }
}

/// <summary>一次扫描的结果。除了设备列表，还带一句给人看的结论。</summary>
public sealed class CameraScanResult
{
    public List<CameraDevice> Devices { get; init; } = new();

    /// <summary>直接显示在设置页上的一句话。</summary>
    public string Summary { get; init; } = "";

    /// <summary>原生库没加载起来 —— 这时候一个设备都测不出来，得单独提示。</summary>
    public bool NativeMissing { get; init; }
}

/// <summary>
/// 枚举本机摄像头。
///
/// 没有可靠的"列出所有摄像头"的托管接口，所以用两步凑：先从注册表拿设备友好名，
/// 再逐个序号试开一次（<see cref="CameraOpener"/> 会顺便预热到出画面）。
/// 试开会占几百毫秒到一秒多，所以必须在后台线程跑（设置页那边是 Task.Run 起来的）。
///
/// 两条必须守住的规则：
/// 1. **当前配置的那个设备试开失败也要留在列表里**。它很可能正被本插件自己占着，
///    试开当然失败；如果因此把它丢掉，用户会看到"没找到可用摄像头"，
///    哪怕摄像头好好的、画面也在正常跑。
/// 2. 原生库没加载起来的时候，每个序号都会失败，而且原因一模一样。
///    这种情况必须明说，不然用户只会以为没有摄像头。
/// </summary>
public static class CameraEnumerator
{
    /// <summary>USB 摄像头的设备类 GUID。</summary>
    private const string CameraClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}";

    /// <summary>
    /// 扫一遍。<paramref name="currentSource"/> 是当前配置里的视频源。
    /// </summary>
    public static CameraScanResult Scan(string? currentSource)
    {
        var names = ReadFriendlyNames();
        var current = int.TryParse(currentSource?.Trim(), out var idx) ? idx : -1;

        var list = new List<CameraDevice>();
        var nativeMissing = false;

        NativeBootstrap.Ensure();

        for (var i = 0; i < CameraOpener.MaxIndex; i++)
        {
            var isCurrent = i == current;
            var result = CameraOpener.OpenIndex(i);

            if (result.Ok)
            {
                var cap = result.Capture!;
                try
                {
                    cap.Release();
                    cap.Dispose();
                }
                catch
                {
                    // 释放失败不影响结论
                }

                list.Add(new CameraDevice
                {
                    Index = i,
                    Name = DisplayName(names, i, isCurrent),
                    Usable = true,
                    Occupied = isCurrent,
                    Note = isCurrent ? "使用中" : ""
                });

                continue;
            }

            if (result.Reason == CameraFailReason.NativeMissing)
            {
                nativeMissing = true;
                break; // 后面每个序号都是同一个错，别浪费时间
            }

            // 当前设备打不开不等于没有：占着它的很可能就是本插件自己。
            // 从注册表条目数推"这个序号上有没有设备"并不可靠（列着不少没插的设备），
            // 所以只说"打不开"，把两种可能都摆给用户。
            if (isCurrent)
            {
                list.Add(new CameraDevice
                {
                    Index = i,
                    Name = DisplayName(names, i, true),
                    Usable = false,
                    Occupied = true,
                    Note = "打不开"
                });
            }
        }

        return new CameraScanResult
        {
            Devices = list,
            NativeMissing = nativeMissing,
            Summary = Summarize(list, nativeMissing, current)
        };
    }

    private static string DisplayName(List<string> names, int index, bool current)
    {
        var name = index < names.Count && !string.IsNullOrWhiteSpace(names[index])
            ? names[index]
            : $"摄像头 {index}";

        return current ? name + "（当前）" : name;
    }

    private static string Summarize(List<CameraDevice> devices, bool nativeMissing, int currentIndex)
    {
        if (nativeMissing)
        {
            return "OpenCvSharp 的原生库没加载起来，所以一个设备都测不出来。"
                   + "这跟摄像头无关：先确认插件包完整（runtimes/win-x64/native 里应有 OpenCvSharpExtern.dll），"
                   + "再看日志里的「原生库」条目。";
        }

        if (devices.Count == 0)
        {
            return "没找到可用摄像头。确认摄像头已插好、驱动正常，并且没有被其它程序独占；"
                   + "也可以直接在下面的视频源里填序号或 rtsp:// 地址。";
        }

        var usable = devices.Count(d => d.Usable);
        var current = devices.FirstOrDefault(d => d.Index == currentIndex);

        var text = usable == devices.Count
            ? $"找到 {devices.Count} 个设备"
            : $"找到 {devices.Count} 个设备，其中 {devices.Count - usable} 个现在打不开";

        if (current is { Usable: false })
        {
            text += "。当前配置的这个打不开 —— 序号填错、被别的程序占着、或者正被本插件自己用着，都有可能";
        }

        return text + "。挑一个会自动填进下面的视频源。";
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
}
