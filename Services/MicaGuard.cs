using System;
using System.IO;

namespace BTAudioSink.Services;

/// <summary>
/// Mica 的安全网。
/// 如果材质在这台机器上没真正生效，窗口会整片变黑，用户连开关都点不到，只能强杀进程。
/// 所以启用 Mica 时写一个标记文件，正常退出时删掉；
/// 下次启动如果发现标记还在，说明上次是异常结束的，自动把 Mica 关掉。
/// </summary>
internal static class MicaGuard
{
    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BTAudioSink", "mica.lock");

    /// <summary>上次启用 Mica 后没有正常退出。</summary>
    public static bool PreviousRunAbnormal => File.Exists(MarkerPath);

    public static void MarkActive()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTime.Now.ToString("O"));
        }
        catch
        {
            // 写不了标记就放弃保护，不影响主流程。
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        }
        catch
        {
        }
    }
}
