using System;
using System.Diagnostics;
using System.IO;

namespace BTAudioSink.Services;

/// <summary>
/// 追加式运行日志，落在 %APPDATA%\BTAudioSink\log.txt。
///
/// 用 .txt 而不是 .log：Windows 对 .log 没有默认关联，双击会弹「选择打开方式」，
/// .txt 则直接落到记事本。界面上那个「日志」按钮打开的就是这个文件。
///
/// 蓝牙这类问题往往要看「点了之后系统回调了什么」，必须能事后翻，
/// 所以文件是持续追加的，不是每轮会话一份。
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();

    /// <summary>日志文件位置。界面上点「日志」打开的就是它。</summary>
    public static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BTAudioSink", "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = LogFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                MigrateLegacyFile(path);

                Rotate(path);

                Append(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写不了不能影响主流程。
        }
    }

    /// <summary>
    /// 轮转：日志超过上限就把当前文件挪成 .1，只保留一份历史。
    ///
    /// 挪动失败（比如用户正用记事本打开着这个文件——重命名要求对方允许删除共享，
    /// 记事本不允许）绝对不能连累后面的追加。否则整个日志会从这一刻起静默消失，
    /// 而这恰恰发生在用户正在排查问题、点「日志」把文件打开的那一刻。
    /// </summary>
    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxBytes) return;

            var backup = path + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(path, backup);
        }
        catch
        {
            // 挪不动就先不轮转，日志继续往当前文件追加。
        }
    }

    /// <summary>
    /// 追加一行。显式带上 FileShare.ReadWrite | FileShare.Delete，
    /// 这样别的程序（记事本、VS Code）正打开着这个文件时也能写进去。
    /// </summary>
    private static void Append(string path, string line)
    {
        using var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream);
        writer.Write(line);
    }

    /// <summary>
    /// 用系统默认的文本编辑器打开日志文件。
    /// 打开不了（没有关联程序等）时退回记事本，再不行返回 false 交给界面提示。
    /// </summary>
    public static bool TryOpen()
    {
        try
        {
            lock (Gate)
            {
                var path = LogFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                MigrateLegacyFile(path);

                // 还没写过（理论上不会发生，启动时总会写一条）时先建一个空文件，
                // 否则打开会报「文件不存在」。
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, string.Empty);
                }
            }

            var startInfo = new ProcessStartInfo(LogFilePath) { UseShellExecute = true };
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            // 退回记事本再试一次。
            try
            {
                Process.Start("notepad.exe", $"\"{LogFilePath}\"");
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 早期版本把日志写在 app.log。首次运行时把它挪过来，别让历史断掉。
    /// </summary>
    private static void MigrateLegacyFile(string path)
    {
        if (File.Exists(path)) return;

        var legacy = Path.Combine(Path.GetDirectoryName(path)!, "app.log");
        if (!File.Exists(legacy)) return;

        try
        {
            File.Move(legacy, path);
        }
        catch
        {
            // 挪不动就算了，不影响主流程。
        }
    }
}
