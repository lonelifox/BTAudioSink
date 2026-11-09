using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BTAudioSink.Services;

/// <summary>
/// 用户偏好，存到 %APPDATA%\BTAudioSink\settings.json。
/// </summary>
public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>窗口使用 Mica 半透明材质。仅 Win11 生效，其他系统自动忽略。</summary>
    public bool UseMica { get; set; } = true;

    /// <summary>外观：自动跟随系统 / 浅色 / 深色。</summary>
    public AppTheme Theme { get; set; } = AppTheme.Auto;

    /// <summary>界面语言。</summary>
    public AppLanguage Language { get; set; } = AppLanguage.Chinese;

    /// <summary>
    /// 开了「自动重连」的设备 ID。逐台设置，不是全局开关：
    /// 列在这里的设备才会在断开后、以及下次启动时自动连回来。
    /// </summary>
    public List<string> AutoReconnectIds { get; set; } = new();

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BTAudioSink");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    // 枚举存成字符串，配置文件手改时不容易填错。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return loaded is null ? new AppSettings() : loaded.Normalized();
        }
        catch
        {
            // 配置损坏时回到默认值，不让程序起不来。
            return new AppSettings();
        }
    }

    /// <summary>
    /// 把可能为 null 的集合补齐。
    ///
    /// System.Text.Json 遇到显式的 JSON null 会把属性置成 null，
    /// 属性初始化器（= new()）会被覆盖掉。所以配置文件里手写一个
    /// "AutoReconnectIds": null，下次启动枚举到第一台设备时 .Contains 就炸，
    /// 而且每次启动都炸——配置不会自愈。
    /// </summary>
    private AppSettings Normalized()
    {
        AutoReconnectIds ??= new List<string>();
        return this;
    }

    public void Save()
    {
        var temp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Folder);
            AutoReconnectIds ??= new List<string>();
            var json = JsonSerializer.Serialize(this, JsonOptions);

            // 先写临时文件再替换掉正式的。直接覆盖写的话，
            // 进程写到一半被杀（结束任务、断电、同步盘冲突）就留下半截 JSON，
            // 下次启动用户的偏好全丢，而且他完全不知道发生过什么。
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // 写配置失败不影响主流程。
            TryDelete(temp);
        }
    }

    /// <summary>删掉可能残留的临时文件。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
