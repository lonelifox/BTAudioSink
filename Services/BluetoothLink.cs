using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BTAudioSink.Services;

/// <summary>设备的蓝牙链路状态。注意这是「蓝牙连没连上」这一层，不是 A2DP 那一层。</summary>
public enum BluetoothLinkState
{
    /// <summary>蓝牙链路已经建立。这时候才谈得上把音频切到 A2DP。</summary>
    Linked,

    /// <summary>
    /// 设备在配对列表里，但没有和这台电脑建立连接。
    ///
    /// 注意：这个状态<b>不能</b>再进一步区分「蓝牙关了」和「蓝牙开着只是没连」。
    /// 曾经想用信号强度分：关掉蓝牙的手机是 -128，已连接的 iPad 是 -6。
    /// 但实测 iPad 就在旁边、蓝牙开着、只是没连时，同样报 -128——
    /// 经典蓝牙的 RSSI 只在搜索/建立连接时才刷新，没有链路就没有新样本。
    /// 所以 -128 只能说明「系统没测到」，不能说明「设备不在」。
    /// 要不要试着连，交给调用方用「尝试次数上限」控制，而不是在这里拦死。
    /// </summary>
    NotLinked,

    /// <summary>查不出来（系统查询失败 / 缺标识）。这种情况不拦自动连接，宁可试一次。</summary>
    Unknown,
}

/// <summary>一台蓝牙设备的详细信息，给界面上的「展开详情」用。</summary>
/// <param name="Address">蓝牙 MAC，形如 <c>9C:9E:D5:4D:55:50</c>；查不到为 null。</param>
/// <param name="IsConnected">蓝牙链路是否建立（不是 A2DP 那一层）。</param>
/// <param name="SignalStrength">系统缓存到的信号强度。没测到时为 0 或 -128，别当真值显示。</param>
/// <param name="ClassMajor">蓝牙设备的主类别码（1=电脑 2=手机 4=音频…）；查不到为 null。</param>
public sealed record BluetoothLinkDetails(
    string? Address,
    bool IsConnected,
    int SignalStrength,
    int? ClassMajor);

/// <summary>
/// 「设备有没有真的连着蓝牙」的检测。
///
/// 为什么要单独查：<c>AudioPlaybackConnection.GetDeviceSelector()</c> 列出的是<b>已配对</b>的设备，
/// 蓝牙没连着时它照样在列表里。此时直接 <c>OpenAsync()</c> 只会一路等到超时，
/// 表现就是「明明没连，软件却一直反复去连」。
///
/// 判断方法：A2DP 设备接口和蓝牙设备共用同一个「容器 ID」（Windows 用来把同一台设备的各种接口归到一起），
/// 拿容器 ID 去已配对蓝牙设备里找到同一台，读它的 <c>System.Devices.Aep.IsConnected</c>。
/// 容器 ID 取不到时退回到「从设备实例 ID 里抠出蓝牙 MAC 地址」再比对。
/// </summary>
public static class BluetoothLink
{
    private static readonly string[] QueryProperties =
    {
        "System.Devices.Aep.ContainerId",
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.IsConnected",
    };

    private static readonly string[] DetailProperties =
    {
        "System.Devices.Aep.ContainerId",
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.SignalStrength",
        // 注意：不要再加 System.Devices.Aep.Bluetooth.LastConnectedTime —— 这个键不存在，
        // 一放进来整次 FindAllAsync 就抛「找不到元素」，详情全部变「—」。
        // 存在的那个叫 LastSeenTime，但它的值比真实时间整整差 8 小时（本地墙钟被塞进了 UTC 槽），
        // 拿不准的东西不显示，所以不用。
        "System.Devices.Aep.Bluetooth.Cod.Major",
    };

    /// <summary>
    /// 查一台设备的详细信息。查询失败或找不到对应设备时返回 null——界面该显示「不可用」，
    /// 而不是编一组默认值出来。
    /// </summary>
    public static async Task<BluetoothLinkDetails?> QueryDetailsAsync(Guid? containerId, string sinkDeviceId)
    {
        var fallbackAddress = AddressFromSinkId(sinkDeviceId);
        if (containerId is null && fallbackAddress is null) return null;

        DeviceInformationCollection paired;
        try
        {
            paired = await DeviceInformation.FindAllAsync(
                BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                DetailProperties,
                DeviceInformationKind.AssociationEndpoint);
        }
        catch (Exception ex)
        {
            AppLog.Write(Loc.Format("Log.DetailQueryFailed", ex.GetType().Name, ex.Message));
            return null;
        }

        foreach (var device in paired)
        {
            if (!IsSameDevice(device, containerId, fallbackAddress)) continue;

            var connected = device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var raw)
                            && raw is bool flag && flag;

            var rssi = device.Properties.TryGetValue("System.Devices.Aep.SignalStrength", out var signal)
                       && signal is int value
                ? value
                : 0;

            var address = device.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var addr)
                          && addr is string text
                ? text
                : fallbackAddress;

            // 类别码可能是 UInt16 也可能是 int，按数值取，别硬转。
            int? classMajor = device.Properties.TryGetValue("System.Devices.Aep.Bluetooth.Cod.Major", out var cod)
                ? Convert.ToInt32(cod)
                : null;

            return new BluetoothLinkDetails(FormatAddress(address), connected, rssi, classMajor);
        }

        return null;
    }

    /// <summary>把 <c>9C9ED54D5550</c> / <c>9c:9e:d5:... </c> 统一成 <c>9C:9E:D5:4D:55:50</c>。</summary>
    private static string? FormatAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;

        var digits = NormalizeAddress(address);
        if (digits.Length != 12) return address;

        var parts = new string[6];
        for (var i = 0; i < 6; i++) parts[i] = digits.Substring(i * 2, 2);
        return string.Join(':', parts);
    }

    /// <summary>
    /// 查一次链路状态。每次都真问系统，不缓存——「现在连没连」是会随时变的。
    /// </summary>
    /// <param name="containerId">A2DP 设备接口的容器 ID（<see cref="AudioSinkService"/> 枚举时记下来的）。</param>
    /// <param name="sinkDeviceId">A2DP 设备接口 ID，容器 ID 缺失时从里面解析 MAC 地址。</param>
    public static async Task<BluetoothLinkState> QueryAsync(Guid? containerId, string sinkDeviceId)
    {
        var address = AddressFromSinkId(sinkDeviceId);
        if (containerId is null && address is null)
        {
            return BluetoothLinkState.Unknown;
        }

        DeviceInformationCollection paired;
        try
        {
            paired = await DeviceInformation.FindAllAsync(
                BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                QueryProperties,
                DeviceInformationKind.AssociationEndpoint);
        }
        catch (Exception ex)
        {
            AppLog.Write(Loc.Format("Log.LinkQueryFailed", ex.GetType().Name, ex.Message));
            return BluetoothLinkState.Unknown;
        }

        foreach (var device in paired)
        {
            if (!IsSameDevice(device, containerId, address)) continue;

            var connected = device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var raw)
                            && raw is bool flag && flag;

            // 这里故意不写日志：自动连接会每两秒查一次，一次一行会把日志淹掉。
            // 状态变化由调用方（MainWindow）在翻转时记一行。
            return connected ? BluetoothLinkState.Linked : BluetoothLinkState.NotLinked;
        }

        // 配对列表里找不到这台设备：界面还列着它，说明接口还在，但它已经不在配对列表里了，
        // 那就不可能连得上，按「未连接」处理。
        return BluetoothLinkState.NotLinked;
    }

    private static bool IsSameDevice(DeviceInformation device, Guid? containerId, string? address)
    {
        if (containerId is { } wanted &&
            device.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var raw) &&
            raw is Guid actual &&
            actual == wanted)
        {
            return true;
        }

        if (address is null) return false;

        return device.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var addr) &&
               addr is string text &&
               NormalizeAddress(text) == address;
    }

    /// <summary>
    /// 从 A2DP 设备接口 ID 里抠出蓝牙 MAC。
    /// 形如 <c>...\7&amp;18f8bc1d&amp;0&amp;9C9ED54D5550_C00000000#{6994ad04-...}\SNK</c>，
    /// 中间那段 12 位十六进制就是设备地址（后面紧跟的那 8 位是固定的端口标识，别一起抓）。
    /// </summary>
    private static string? AddressFromSinkId(string sinkDeviceId)
    {
        var match = Regex.Match(sinkDeviceId ?? string.Empty, @"&([0-9A-Fa-f]{12})_[0-9A-Fa-f]{8}",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string NormalizeAddress(string address) =>
        address.Replace(":", string.Empty).Replace("-", string.Empty).ToUpperInvariant();
}
