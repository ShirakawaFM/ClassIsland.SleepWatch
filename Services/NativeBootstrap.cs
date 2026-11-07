using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ClassIsland.SleepWatch.Services;

/// <summary>
/// 原生库加载引导。
///
/// 插件运行在独立的 AssemblyLoadContext 里，托管 DLL 由 ClassIsland 负责加载，
/// 但 OpenCvSharp 底下的 OpenCvSharpExtern.dll 是通过 P/Invoke 找的，
/// 默认只会去搜 ClassIsland 自己的目录 —— 那里没有它，于是直接 DllNotFoundException。
///
/// 这里给 OpenCvSharp 程序集装一个 DllImport 解析器，按顺序去插件目录、
/// 以及 runtimes/win-x64/native 里找。只装一次，失败不影响托管部分。
/// </summary>
public static class NativeBootstrap
{
    private static readonly object Gate = new();
    private static bool _installed;

    public static void Ensure()
    {
        lock (Gate)
        {
            if (_installed) return;

            try
            {
                var dirs = CandidateDirs();

                NativeLibrary.SetDllImportResolver(
                    typeof(OpenCvSharp.Mat).Assembly,
                    (name, assembly, searchPath) =>
                    {
                        foreach (var dir in dirs)
                        {
                            if (string.IsNullOrWhiteSpace(dir)) continue;

                            var candidate = Path.Combine(dir, name + ".dll");
                            if (File.Exists(candidate))
                            {
                                return NativeLibrary.Load(candidate);
                            }

                            candidate = Path.Combine(dir, name);
                            if (File.Exists(candidate))
                            {
                                return NativeLibrary.Load(candidate);
                            }
                        }

                        return IntPtr.Zero; // 交回默认解析
                    });

                _installed = true;
                AppLog.Info("原生库解析器已安装，搜索目录：" + string.Join(" | ", dirs));
            }
            catch (Exception ex)
            {
                // 不要在这里标记已安装：抛异常多半是这一次没装成，
                // 下次调用还该再试一遍，否则后续所有 P/Invoke 都会按失败路径走。
                AppLog.Warn("原生库解析器安装失败：" + ex.Message);
            }
        }
    }

    /// <summary>
    /// 真的去加载一次 OpenCvSharpExtern，返回 null 表示正常，否则是原因。
    /// 设置页在“一个设备都测不出来”时用它区分「没摄像头」和「原生库没起来」。
    /// </summary>
    public static string? Probe()
    {
        Ensure();

        try
        {
            if (NativeLibrary.TryLoad("OpenCvSharpExtern", typeof(OpenCvSharp.Mat).Assembly, null, out var handle)
                && handle != IntPtr.Zero)
            {
                return null;
            }

            return "OpenCvSharpExtern.dll 没找到（插件包可能不完整）";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static List<string> CandidateDirs()
    {
        var dirs = new List<string>();

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (!dirs.Contains(dir)) dirs.Add(dir);
        }

        Add(ModelManager.AssemblyDir);
        Add(Path.Combine(ModelManager.AssemblyDir, "runtimes", "win-x64", "native"));
        Add(Path.Combine(ModelManager.AssemblyDir, "runtimes", "win", "native"));

        Add(AppContext.BaseDirectory);
        Add(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native"));

        return dirs;
    }
}
