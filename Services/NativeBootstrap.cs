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
            _installed = true;

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
            }
            catch (Exception ex)
            {
                // 已经装过解析器、或者运行时不允许 —— 都不影响继续尝试默认路径
                Console.WriteLine("[SleepWatch] 原生库解析器安装失败：" + ex.Message);
            }
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
