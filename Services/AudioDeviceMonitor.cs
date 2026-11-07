using System;
using System.Threading;

using Microsoft.Win32;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 麦克风占用监测。
///
/// 教室里经常同时开着直播、录课、语音互动，这些程序会抓住麦克风。
/// 本插件自己并不录音，但视频采集和音频采集在总线上会互相挤压，
/// 别人在用麦克风的时候主动把帧率降下来，冲突会少很多。
///
/// 检测手段用的是 Windows 自己的"能力访问"记录：
/// <c>ConsentStore\microphone</c> 下面每个程序都有 LastUsedTimeStart / LastUsedTimeStop，
/// Start 比 Stop 新就说明它此刻正在用。这是系统给隐私指示器（托盘那个小绿点）用的同一份数据，
/// 不需要驱动、不需要管理员权限，也不会真的去打开设备。
///
/// 读不到（系统版本不同、键不存在）就自动禁用这个功能，只写日志，不打扰用户。
/// </summary>
public static class AudioDeviceMonitor
{
    private const string ConsentRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private static readonly object Gate = new();

    private static Timer? _timer;
    private static bool _busy;
    private static int _failures;

    /// <summary>当前是否有别的程序占着麦克风。</summary>
    public static bool IsBusy
    {
        get
        {
            lock (Gate) return _busy;
        }
    }

    /// <summary>这套系统上能不能读。读不到就一直是 false，功能整体停用。</summary>
    public static bool Supported { get; private set; } = true;

    /// <summary>占用状态变化时会触发。参数是"现在是否被占用"。</summary>
    public static event Action<bool>? BusyChanged;

    public static void Start(int intervalSeconds = 2)
    {
        lock (Gate)
        {
            if (_timer != null) return;

            var period = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 1, 10));
            _timer = new Timer(_ => Poll(), null, period, period);
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _timer?.Dispose();
            _timer = null;
            _busy = false;
        }
    }

    private static void Poll()
    {
        if (!Supported) return;

        bool busy;

        try
        {
            busy = QueryBusy();
            _failures = 0;
        }
        catch (Exception ex)
        {
            // 连续三次读不到就当这套系统不支持，彻底关掉，别一直刷日志
            if (++_failures >= 3)
            {
                Supported = false;
                Stop();
                AppLog.Warn("麦克风占用检测不可用，已停用该功能：" + ex.Message);
            }

            return;
        }

        bool changed;

        lock (Gate)
        {
            changed = _busy != busy;
            _busy = busy;
        }

        if (!changed) return;

        AppLog.Debug(busy ? "检测到麦克风被占用，进入降频礼让" : "麦克风占用解除，恢复原帧率");

        try
        {
            BusyChanged?.Invoke(busy);
        }
        catch (Exception ex)
        {
            AppLog.Error("麦克风占用回调失败", ex);
        }
    }

    /// <summary>逐个看 ConsentStore 下的 LastUsedTimeStart / Stop。</summary>
    private static bool QueryBusy()
    {
        using var root = Registry.CurrentUser.OpenSubKey(ConsentRoot);
        if (root == null) return false;

        if (IsActive(root)) return true;

        using var nonPackaged = root.OpenSubKey("NonPackaged");
        if (nonPackaged == null) return false;

        foreach (var name in nonPackaged.GetSubKeyNames())
        {
            using var app = nonPackaged.OpenSubKey(name);
            if (app != null && IsActive(app)) return true;
        }

        return false;
    }

    private static bool IsActive(RegistryKey key)
    {
        var start = ReadLong(key, "LastUsedTimeStart");
        var stop = ReadLong(key, "LastUsedTimeStop");

        // Start 为 0 表示从来没打开过；Stop 为 0 表示还没释放
        if (start <= 0) return false;

        return stop <= 0 || start > stop;
    }

    private static long ReadLong(RegistryKey key, string name)
    {
        var value = key.GetValue(name);

        return value switch
        {
            long l => l,
            int i => i,
            byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
            _ => 0
        };
    }
}
