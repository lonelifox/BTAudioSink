using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BTAudioSink.Services;

/// <summary>
/// 电量新鲜度缓存。
///
/// <para><b>为什么要自己记时间</b>：Windows 只给「当前电量值」，不告诉你是刚读到的
/// 还是几天前的残留。设备一断开，属性还留在那里，界面上看起来跟实时值一模一样。</para>
///
/// <para>规则：设备**连接状态下**读到电量时刷新时间戳；断开后沿用上次时间戳。
/// 超过一小时没更新过的值在界面上置灰，表示「这是旧数据」。</para>
///
/// <para>时间戳落盘，所以重启程序也认得出来——否则每次开机都从「刚刚」算起，
/// 一小时的门槛永远触发不了。</para>
/// </summary>
internal static class BatteryFreshness
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BTAudioSink", "battery.json");

    private static Dictionary<string, Entry>? _cache;

    private sealed record Entry(int Percent, DateTime UpdatedUtc);

    private static Dictionary<string, Entry> Cache =>
        _cache ??= Load();

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var text = File.ReadAllText(CachePath);
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(text)
                       ?? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* 缓存坏了就当空的，不影响主流程 */ }

        return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>设备连着时调用：把这一刻记为「刚更新」。</summary>
    public static void MarkFresh(string containerId, int percent)
    {
        Cache[containerId] = new Entry(percent, DateTime.UtcNow);
        Dirty = true;
    }

    /// <summary>设备没连时调用：判断这个值是不是已经放了太久。</summary>
    public static bool IsStale(string containerId)
    {
        if (!Cache.TryGetValue(containerId, out var entry)) return true;   // 没有记录 = 来源不明
        return DateTime.UtcNow - entry.UpdatedUtc > StaleAfter;
    }

    public static bool Dirty { get; private set; }

    /// <summary>把时间戳写回磁盘。只在有变化时调用。</summary>
    public static void Save()
    {
        if (!Dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(Cache));
            Dirty = false;
        }
        catch { /* 写不进就算了，下次再试 */ }
    }
}
