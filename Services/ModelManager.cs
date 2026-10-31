using System;
using System.Collections.Generic;
using System.IO;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 模型定位。onnx 直接随插件一起分发，不放下载逻辑：
/// 教室机器经常没有外网，运行时再拉模型是最容易翻车的一步。
///
/// 注意：不能拿 AppContext.BaseDirectory 找模型。插件是加载进 ClassIsland 进程里的，
/// 那个路径指向的是 ClassIsland 安装目录，不是插件自己的目录。
/// 正确做法是用本程序集自己的位置（Assembly.Location）。
///
/// 查找顺序：插件目录\models → 插件配置目录\models → ClassIsland 目录\models。
/// 想换模型（比如换成 yolov8s-pose 提精度）直接替换文件即可。
/// </summary>
public static class ModelManager
{
    public const string PoseFile = "yolov8n-pose.onnx";

    /// <summary>插件 DLL 所在目录。</summary>
    public static string AssemblyDir
    {
        get
        {
            try
            {
                var location = typeof(ModelManager).Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    var dir = Path.GetDirectoryName(location);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            catch
            {
                // 少数发布方式拿不到 Location，走兜底
            }

            return AppContext.BaseDirectory;
        }
    }

    /// <summary>
    /// 每次调用重新计算。不要做成静态字段：
    /// 那样会在 Plugin.Initialize 设置 ConfigFolder 之前就把空路径缓存下来。
    /// </summary>
    private static IEnumerable<string> SearchRoots()
    {
        yield return Path.Combine(AssemblyDir, "models");
        yield return Path.Combine(Plugin.ConfigFolder, "models");
        yield return Path.Combine(AppContext.BaseDirectory, "models");
    }

    public static string? Resolve(string fileName)
    {
        foreach (var root in SearchRoots())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            try
            {
                var path = Path.Combine(root, fileName);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 50_000) return fi.FullName;
            }
            catch
            {
                // 路径异常就跳过
            }
        }

        return null;
    }

    public static bool PoseReady => Resolve(PoseFile) != null;

    public static string Describe() => PoseReady ? "已就绪" : "缺失";

    /// <summary>找不到模型时，把实际找过的路径列出来，省得用户猜。</summary>
    public static string DescribeSearch()
    {
        var lines = new List<string>();

        foreach (var root in SearchRoots())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            lines.Add("  " + Path.Combine(root, PoseFile));
        }

        return string.Join("\n", lines);
    }
}
