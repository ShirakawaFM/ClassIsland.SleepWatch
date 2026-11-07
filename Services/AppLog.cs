using System;
using System.IO;
using System.Text;

namespace ClassIsland.SleepWatch.Services;

/// <summary>日志分级。数值越大越啰嗦，配置里存的是名字。</summary>
public enum LogLevel
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3
}

/// <summary>
/// 轻量分级日志。写到配置目录的 logs 子目录，按天切文件、单文件超过 2 MB 就换下一个。
///
/// 为什么不用 Console / Debug：插件跑在 ClassIsland 进程里，教室机器上没人会开调试器，
/// 出问题只能事后翻文件。所有 IO 都包了 try，写日志本身绝不能把检测搞崩。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    private const long MaxBytes = 2 * 1024 * 1024;

    private static LogLevel _level = LogLevel.Info;
    private static DateTime _fileDate = DateTime.MinValue;
    private static int _serial;

    public static LogLevel Level
    {
        get => _level;
        set => _level = value;
    }

    /// <summary>从配置里读一次级别。配置缺失或写错就维持 Info。</summary>
    public static void Configure(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        if (Enum.TryParse<LogLevel>(name.Trim(), ignoreCase: true, out var level))
        {
            _level = level;
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message, Exception? error = null)
    {
        Write(LogLevel.Error, error == null ? message : $"{message} :: {error.GetType().Name}: {error.Message}");
    }

    public static void Error(string message, string detail)
        => Write(LogLevel.Error, $"{message} :: {detail}");

    private static void Write(LogLevel level, string message)
    {
        if (level > _level) return;

        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Tag(level)}] {message}{Environment.NewLine}";

            lock (Gate)
            {
                var path = CurrentFile();

                // 超过上限就换序号，别让一个文件长到几十兆打不开
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    _serial++;
                    path = CurrentFile();
                }

                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去也不能影响检测
        }
    }

    private static string CurrentFile()
    {
        var today = DateTime.Today;
        if (today != _fileDate) _fileDate = today;

        var dir = Path.Combine(Plugin.ConfigFolder, "logs");
        Directory.CreateDirectory(dir);

        var name = _serial <= 0
            ? $"sleepwatch-{today:yyyyMMdd}.log"
            : $"sleepwatch-{today:yyyyMMdd}-{_serial}.log";

        return Path.Combine(dir, name);
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Error => "ERR ",
        LogLevel.Warn => "WARN",
        LogLevel.Debug => "DBG ",
        _ => "INFO"
    };
}
