using System;
using Microsoft.Win32;

namespace BTAudioSink.Services;

/// <summary>
/// 通过 HKCU 的 Run 键控制开机自启动，不需要管理员权限。
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BTAudioSink";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <param name="exePath">当前可执行文件路径，通常取 Environment.ProcessPath。</param>
    public static void SetEnabled(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null) return;

            if (enabled)
            {
                // --minimized 让程序开机时直接缩到托盘，不弹窗打扰。
                key.SetValue(ValueName, $"\"{exePath}\" --minimized");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 组策略可能禁用 Run 键写入，失败时保持原状即可。
        }
    }
}
