using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;

namespace BTAudioSink.Services;

/// <summary>
/// 输出侧蓝牙音频设备（耳机 / 音箱）的枚举、连接与断开。
///
/// <para><b>为什么单独有一层</b>：本程序本职是把电脑变成「音箱」（A2DP Sink），
/// 面板里列的是**往本机送音频**的设备。耳机的方向相反，是**本机把声音送出去**，
/// 两者不能混在一个列表里。</para>
///
/// <para><b>连接机制</b>：Windows 驱动文档在 <c>ksmedia.h</c> 里定义了
/// <c>KSPROPERTY_BTAUDIO</c> 的两个属性：<c>KSPROPERTY_ONESHOT_RECONNECT</c>(0) 与
/// <c>KSPROPERTY_ONESHOT_DISCONNECT</c>(1)，作用是「请求音频驱动尝试连接 / 断开蓝牙音频设备」。
/// 音频驱动收到后转成内核请求，是系统自己的正常路径。</para>
///
/// <para><b>限制</b>：这条通道只有在设备已经连着的时候才存在（音频过滤器要等设备起来才创建）。
/// 所以「连接」得先让系统去激活端点：把默认输出切到它，音频服务就会去拉链路；
/// 仍不成功时再退回 Win32 的蓝牙服务启用。</para>
///
/// <para><b>许可证</b>：本文件按上述公开契约自行实现，未抄录任何第三方项目代码。</para>
/// </summary>
public static class BluetoothOutputService
{
    private const int DeviceStateMaskAll = 0x0000000F;
    private const int DeviceStateActive = 0x00000001;
    private const string KsCategoryAudio = "{6994AD04-93EF-11D0-A3CC-00A0C9223196}";

    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private static readonly string[] BluetoothAudioServices =
    {
        "{0000110B-0000-1000-8000-00805F9B34FB}",   // A2DP Sink —— 耳机 / 音箱
        "{0000111E-0000-1000-8000-00805F9B34FB}",   // Handsfree
        "{00001108-0000-1000-8000-00805F9B34FB}",   // Headset
    };

    private static readonly PropertyKey PkeyContainerId = new()
    {
        FormatId = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        Pid = 2,
    };

    private static readonly PropertyKey PkeyFriendlyName = new()
    {
        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        Pid = 14,
    };

    /// <summary>PKEY_BluetoothBatteryLevel —— 系统设置页显示的那个百分比就是它。</summary>
    private const string BatteryPropertyKey = "{104ea319-6ee2-4701-bd47-8ddbf425bbe5} 2";

    private static readonly Guid KsPropSetIdBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private const uint KsPropertyOneShotReconnect = 0;
    private const uint KsPropertyOneShotDisconnect = 1;
    private const uint KsPropertyTypeGet = 0x00000001;

    // ---------- 对外模型 ----------

    public sealed record OutputDevice(
        string ContainerId,
        string Name,
        string Address,
        string EndpointId,
        bool IsConnected,
        bool CanControl,
        IReadOnlyList<string> Profiles,
        int? BatteryPercent,
        bool BatteryStale,
        string? Codec);

    // ---------- 枚举 ----------

    /// <summary>列出所有已配对的蓝牙输出设备（含当前没连上的）。</summary>
    public static async Task<IReadOnlyList<OutputDevice>> EnumerateAsync()
    {
        var index = await BuildBluetoothIndexAsync().ConfigureAwait(false);
        if (index.Count == 0) return Array.Empty<OutputDevice>();

        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = CreateEnumerator();
            if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceStateMaskAll, out var collection) < 0
                || collection is null)
                return Array.Empty<OutputDevice>();

            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) < 0 || device is null) continue;
                try { TryAddEndpoint(enumerator, device, index, groups); }
                finally { Release(device); }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(Loc.LogFormat("Log.OutputEnumFailed", ex.Message));
            return Array.Empty<OutputDevice>();
        }
        finally
        {
            Release(enumerator);
        }

        // 电量新鲜度：连接时刷新时间戳，断开后超过一小时算旧值。
        foreach (var group in groups.Values)
        {
            if (group.Battery is int percent)
            {
                if (group.Active)
                {
                    BatteryFreshness.MarkFresh(group.ContainerId, percent);
                    group.BatteryStale = false;
                }
                else
                {
                    group.BatteryStale = BatteryFreshness.IsStale(group.ContainerId);
                }
            }
        }
        BatteryFreshness.Save();

        return groups.Values
            .Select(g => new OutputDevice(
                g.ContainerId,
                g.Name.Length > 0 ? g.Name : Loc.Get("Output.UnknownName"),
                g.Address,
                g.EndpointId,
                g.Active,
                g.KsControls.Count > 0,
                g.Profiles,
                g.Battery,
                g.BatteryStale,
                g.Codec))
            .OrderByDescending(d => d.IsConnected)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>同步包装，给不便 await 的调用点（内部已切到线程池，不会卡 UI）。</summary>
    public static IReadOnlyList<OutputDevice> Enumerate() =>
        Task.Run(EnumerateAsync).GetAwaiter().GetResult();

    private sealed class Group
    {
        public string ContainerId = string.Empty;
        public string Name = string.Empty;
        public string Address = string.Empty;
        public string EndpointId = string.Empty;
        public bool Active;
        public double Rate;
        public int? Battery;
        public bool BatteryStale;
        public string? Codec;
        public readonly List<string> Profiles = new();
        public readonly List<IKsControl> KsControls = new();
    }

    private static void TryAddEndpoint(IMMDeviceEnumerator enumerator, IMMDevice device,
        IReadOnlyDictionary<string, BluetoothEntry> index, Dictionary<string, Group> groups)
    {
        if (device.OpenPropertyStore(0, out var props) < 0 || props is null) return;

        string container;
        string friendly;
        try
        {
            container = Normalize(ReadGuid(props, PkeyContainerId)?.ToString("D"));
            friendly = ReadString(props, PkeyFriendlyName) ?? string.Empty;
        }
        finally { Release(props); }

        // 只保留蓝牙音频端点：容器必须出现在蓝牙服务索引里。
        // 这一步同时排除了板载声卡、USB 声卡这些同样叫「耳机」的端点。
        if (container.Length == 0 || !index.TryGetValue(container, out var entry)) return;

        string endpointId = device.GetId(out string id2) >= 0 ? id2 : string.Empty;
        device.GetState(out uint state);
        bool active = state == DeviceStateActive;

        var ks = FindKsControl(enumerator, device, out string ksDeviceId);

        if (!groups.TryGetValue(container, out var group))
        {
            group = new Group
            {
                ContainerId = container,
                Name = entry.Name,
                Address = entry.Address,
                Battery = entry.Battery,
                Codec = ReadCodec(entry.Address),
            };
            groups[container] = group;
        }

        if (active) group.Active = true;
        if (ksDeviceId.Length > 0) group.Profiles.Add(ProfileLabel(ksDeviceId));
        if (ks is not null) group.KsControls.Add(ks);

        // 连接时优先用 A2DP 端点（立体声那个），免手端点音质差，只作兜底。
        bool isA2dp = friendly.Contains("Stereo", StringComparison.OrdinalIgnoreCase)
                      || friendly.Contains("立体声", StringComparison.Ordinal);
        double rate = (isA2dp ? 2 : 1) + (active ? 4 : 0) + (ks is not null ? 1 : 0);
        if (rate > group.Rate)
        {
            group.Rate = rate;
            group.EndpointId = endpointId;
        }
    }

    // ---------- 蓝牙设备索引（WinRT） ----------

    private sealed record BluetoothEntry(string Name, string Address, int? Battery);

    /// <summary>
    /// 用设备接口枚举拿到「蓝牙音频设备 → 容器 ID」的对应关系。
    /// 端点侧读到的 <c>ContainerId</c> 与这里的 <c>System.Devices.ContainerId</c> 是同一个值，
    /// 这就是两边能对上号的原因。
    /// </summary>
    private static async Task<Dictionary<string, BluetoothEntry>> BuildBluetoothIndexAsync()
    {
        var result = new Dictionary<string, BluetoothEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string service in BluetoothAudioServices)
        {
            string aqs =
                $@"System.Devices.DevObjectType:=8 AND " +
                $@"System.Devices.InterfaceClassGuid:=""{KsCategoryAudio}"" AND " +
                $@"System.DeviceInterface.Bluetooth.ServiceGuid:=""{service}""";

            DeviceInformationCollection list;
            try { list = await DeviceInformation.FindAllAsync(aqs); }
            catch { continue; }

            foreach (var info in list)
            {
                if (!info.Properties.TryGetValue("System.Devices.ContainerId", out object? raw)) continue;
                string container = Normalize(raw?.ToString());
                if (container.Length == 0) continue;
                if (result.ContainsKey(container)) continue;

                string instanceId = info.Properties.TryGetValue("System.Devices.DeviceInstanceId", out object? id)
                    ? id?.ToString() ?? string.Empty
                    : string.Empty;

                result[container] = new BluetoothEntry(info.Name, ExtractAddress(instanceId), null);
            }
        }
        // 再补一遍「已配对蓝牙设备」：有些耳机没有暴露出音频服务接口，
        // 但系统里仍在已配对列表中，靠设备类别（CoD 主类 = 4，音频/视频）识别。
        try
        {
            var paired = await DeviceInformation.FindAllAsync(
                Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                new[] { "System.Devices.Aep.ContainerId", "System.Devices.Aep.DeviceAddress",
                        "System.Devices.Aep.Bluetooth.Cod.Major", BatteryPropertyKey },
                DeviceInformationKind.AssociationEndpoint);

            foreach (var info in paired)
            {
                if (!info.Properties.TryGetValue("System.Devices.Aep.Bluetooth.Cod.Major", out object? cod)) continue;
                if (Convert.ToInt32(cod) != 4) continue;   // 4 = Audio/Video

                string container = Normalize(info.Properties.TryGetValue("System.Devices.Aep.ContainerId", out object? c)
                    ? c?.ToString()
                    : null);
                if (container.Length == 0 || result.ContainsKey(container)) continue;

                string address = info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out object? a)
                    ? a?.ToString() ?? string.Empty
                    : string.Empty;

                int? battery = null;
                if (info.Properties.TryGetValue(BatteryPropertyKey, out object? rawBattery) && rawBattery is not null)
                {
                    try { battery = Convert.ToInt32(rawBattery); }
                    catch { battery = null; }
                }
                if (battery is < 0 or > 100) battery = null;

                result[container] = new BluetoothEntry(info.Name, address, battery);
            }
        }
        catch { /* 补全失败不影响主路径 */ }

        // 电量挂在设备的子节点上（实测是 Hands-Free AG），主节点没有这个属性，
        // 所以统一按容器 ID 从设备节点扫一遍再回填。
        try
        {
            var batteries = BluetoothBatteryReader.ReadAll();
            foreach (var key in result.Keys.ToList())
            {
                if (batteries.TryGetValue(key, out int percent))
                    result[key] = result[key] with { Battery = percent };
            }
        }
        catch { /* 电量为可选信息，失败不影响列表 */ }

        return result;
    }

    /// <summary>容器 ID 统一成不带花括号的小写形式，两边才对得上。</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string trimmed = value.Trim().Trim('{', '}');
        return Guid.TryParse(trimmed, out var guid) ? guid.ToString("D") : trimmed.ToLowerInvariant();
    }

    private static string ExtractAddress(string instanceId)
    {
        // 例：BTHENUM\{0000110b-...}\7&18f8bc1d&0&407218C8E510_C00000000
        // MAC 只在最后一段的 "&0&" 之后，不能从头扫十六进制，否则会抓到服务 GUID。
        var match = System.Text.RegularExpressions.Regex.Match(
            instanceId, @"&(?:amp;)?0&([0-9A-Fa-f]{12})_");
        if (!match.Success)
            match = System.Text.RegularExpressions.Regex.Match(instanceId, @"([0-9A-Fa-f]{12})_C[0-9A-Fa-f]{8}");
        if (!match.Success) return string.Empty;

        string mac = match.Groups[1].Value.ToUpperInvariant();
        if (mac.Distinct().Count() < 4) return string.Empty;
        return string.Join(":", Enumerable.Range(0, 6).Select(n => mac.Substring(n * 2, 2)));
    }

    /// <summary>
    /// 读当前生效的 A2DP 编码。
    ///
    /// <para>来源是 <c>BthPort\Parameters\Keys\&lt;MAC&gt;</c> 下的 <c>A2dpCodec</c>，
    /// 这是驱动协商完成后写回的**实际值**，不是设备支持列表。</para>
    ///
    /// <para><b>限制</b>：该键的 ACL 只允许 SYSTEM 读取，普通权限会拿到
    /// UnauthorizedAccessException。所以这里读不到就返回 null，界面不显示，
    /// 不去猜也别退回「设备支持哪些编码」——那是另一个问题。</para>
    /// </summary>
    private static string? ReadCodec(string address)
    {
        if (_codecAccessDenied || address.Length == 0) return null;

        string mac = address.Replace(":", string.Empty);
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Default);
            using var key = baseKey.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\BthPort\Parameters\Keys\{mac}");
            if (key?.GetValue("A2dpCodec") is not int codec) return null;

            return codec switch
            {
                1 => "SBC",
                2 => "MPEG",
                4 => "AAC",
                8 => "aptX",
                16 => "aptX HD",
                32 => "LDAC",
                _ => null,
            };
        }
        catch (Exception)
        {
            // 该键只允许 SYSTEM 读，普通权限会抛 SecurityException /
            // UnauthorizedAccessException。只提示一次，之后不再尝试。
            _codecAccessDenied = true;
            AppLog.Write(Loc.LogText("Log.CodecDenied"));
            return null;
        }
    }

    private static bool _codecAccessDenied;

    private static string ProfileLabel(string ksDeviceId)
    {
        if (ksDeviceId.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase)) return "HFP";
        if (ksDeviceId.Contains("bthenum", StringComparison.OrdinalIgnoreCase)) return "A2DP";
        return "BT";
    }

    // ---------- 连接 / 断开 ----------

    /// <summary>
    /// 连接或断开一台设备，返回最终是否达成目标状态。
    ///
    /// <para>断开：走 KS oneshot（设备已连接，通道必然存在），不改任何配置。</para>
    /// <para>连接：先试 KS oneshot；通道不存在时改用「切默认输出」促使系统激活端点，
    /// 仍不行再退回蓝牙服务启用。三条路依次降级，日志里能看到实际走了哪条。</para>
    /// </summary>
    public static async Task<bool> SetConnectedAsync(string containerId, bool connect,
        TimeSpan timeout, CancellationToken token = default)
    {
        var snapshot = await EnumerateAsync().ConfigureAwait(false);
        var device = snapshot.FirstOrDefault(d =>
            string.Equals(d.ContainerId, containerId, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            AppLog.Write(Loc.LogText("Log.OutputNotFound"));
            return false;
        }

        if (device.IsConnected == connect) return true;

        if (!connect)
        {
            if (!TrySendOneShot(containerId, connect: false, out string error))
            {
                AppLog.Write(Loc.LogFormat("Log.OutputDisconnectFailed", error));
                return false;
            }
        }
        else
        {
            bool started = TrySendOneShot(containerId, connect: true, out string ksError);
            if (!started)
            {
                AppLog.Write(Loc.LogFormat("Log.OutputWakeFallback", ksError));
                bool policyOk = device.EndpointId.Length > 0 &&
                                AudioEndpointPolicy.SetDefaultEndpoint(device.EndpointId, 1, out _);
                bool radioOk = false;
                if (!policyOk && device.Address.Length > 0)
                    radioOk = BluetoothRadioService.EnableAudioServices(device.Address, out _);

                if (!policyOk && !radioOk)
                {
                    AppLog.Write(Loc.LogText("Log.OutputWakeFailed"));
                    return false;
                }
            }
        }

        // 驱动是异步的，轮询到状态真的变了为止。
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var current = (await EnumerateAsync().ConfigureAwait(false)).FirstOrDefault(d =>
                string.Equals(d.ContainerId, containerId, StringComparison.OrdinalIgnoreCase));
            if (current is not null && current.IsConnected == connect) return true;
            await Task.Delay(300, token).ConfigureAwait(false);
        }

        var last = (await EnumerateAsync().ConfigureAwait(false)).FirstOrDefault(d =>
            string.Equals(d.ContainerId, containerId, StringComparison.OrdinalIgnoreCase));
        return last is not null && last.IsConnected == connect;
    }

    private static bool TrySendOneShot(string containerId, bool connect, out string error)
    {
        error = string.Empty;
        var controls = new List<IKsControl>();
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = CreateEnumerator();
            if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceStateMaskAll, out var collection) < 0
                || collection is null)
            {
                error = Loc.LogText("Log.OutputEnumEndpointsFailed");
                return false;
            }

            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) < 0 || device is null) continue;
                try
                {
                    if (device.OpenPropertyStore(0, out var props) < 0 || props is null) continue;
                    string id;
                    try { id = Normalize(ReadGuid(props, PkeyContainerId)?.ToString("D")); }
                    finally { Release(props); }

                    if (!string.Equals(id, containerId, StringComparison.OrdinalIgnoreCase)) continue;
                    var ks = FindKsControl(enumerator, device, out _);
                    if (ks is not null) controls.Add(ks);
                }
                finally { Release(device); }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally { Release(enumerator); }

        if (controls.Count == 0) { error = Loc.LogText("Log.OutputNoControl"); return false; }

        var property = new KsProperty
        {
            Set = KsPropSetIdBtAudio,
            Id = connect ? KsPropertyOneShotReconnect : KsPropertyOneShotDisconnect,
            Flags = KsPropertyTypeGet,
        };

        int sent = 0;
        foreach (var ks in controls)
        {
            try
            {
                if (ks.KsProperty(ref property, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _) >= 0)
                    sent++;
            }
            catch { /* 单个通道失败不影响其它 */ }
            finally { Release(ks); }
        }

        if (sent == 0) { error = Loc.LogText("Log.OutputRejected"); return false; }
        return true;
    }

    /// <summary>找端点背后的 KS 过滤器。设备未连接时会拿不到，属正常。</summary>
    private static IKsControl? FindKsControl(IMMDeviceEnumerator enumerator, IMMDevice device, out string deviceId)
    {
        deviceId = string.Empty;
        Guid topologyIid = typeof(IDeviceTopology).GUID;
        if (device.Activate(in topologyIid, 1, IntPtr.Zero, out object? topologyObj) < 0
            || topologyObj is not IDeviceTopology topology)
            return null;

        try
        {
            topology.GetConnectorCount(out uint connectorCount);
            for (uint i = 0; i < connectorCount; i++)
            {
                if (topology.GetConnector(i, out var connector) < 0 || connector is null) continue;
                try
                {
                    if (connector.GetConnectedTo(out var other) < 0 || other is null) continue;
                    try
                    {
                        if (other is not IPart part) continue;
                        if (part.GetTopologyObject(out var otherTopology) < 0 || otherTopology is null) continue;
                        try
                        {
                            if (otherTopology.GetDeviceId(out string id) < 0) continue;
                            if (!id.Contains(@"\bth", StringComparison.OrdinalIgnoreCase)) continue;
                            deviceId = id;

                            if (enumerator.GetDevice(id, out var ksDevice) < 0 || ksDevice is null) continue;
                            try
                            {
                                Guid ksIid = typeof(IKsControl).GUID;
                                if (ksDevice.Activate(in ksIid, 1, IntPtr.Zero, out object? ksObj) < 0
                                    || ksObj is not IKsControl ks)
                                    continue;
                                return ks;
                            }
                            finally { Release(ksDevice); }
                        }
                        finally { Release(otherTopology); }
                    }
                    finally { Release(other); }
                }
                finally { Release(connector); }
            }
        }
        finally { Release(topology); }
        return null;
    }

    /// <summary>列出所有渲染端点（含非蓝牙），排查「声音去哪儿了」时用。</summary>
    public static IReadOnlyList<string> ListRenderEndpoints()
    {
        var lines = new List<string>();
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = CreateEnumerator();
            if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceStateMaskAll, out var col) < 0 || col is null)
                return lines;
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var ep) < 0 || ep is null) continue;
                try
                {
                    ep.GetId(out string id);
                    ep.GetState(out uint state);
                    if (ep.OpenPropertyStore(0, out var ps) < 0 || ps is null) continue;
                    string name;
                    try { name = ReadString(ps, PkeyFriendlyName) ?? "?"; }
                    finally { Release(ps); }
                    lines.Add($"{state}  {name}  {id}");
                }
                finally { Release(ep); }
            }
        }
        finally { Release(enumerator); }
        return lines;
    }

    // ---------- 诊断 ----------

    /// <summary>只读诊断：把枚举结果逐条记下来，排查「面板里没有设备」时用。</summary>
    public static IReadOnlyList<string> Diagnose()
    {
        var lines = new List<string>();
        try
        {
            var index = Task.Run(BuildBluetoothIndexAsync).GetAwaiter().GetResult();
            lines.Add($"蓝牙音频容器数={index.Count}");
            foreach (var kv in index)
                lines.Add($"  {kv.Value.Name}  container={kv.Key}  addr={kv.Value.Address}");

            // 原始端点与容器对照，用来判断「端点 ↔ 蓝牙索引」到底卡在哪
            var enumerator = CreateEnumerator();
            try
            {
                if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceStateMaskAll, out var col) >= 0 && col is not null)
                {
                    col.GetCount(out uint n);
                    lines.Add($"渲染端点总数={n}");
                    for (uint i = 0; i < n; i++)
                    {
                        if (col.Item(i, out var ep) < 0 || ep is null) continue;
                        try
                        {
                            if (ep.OpenPropertyStore(0, out var ps) < 0 || ps is null) continue;
                            string cid, nm;
                            try
                            {
                                cid = ReadGuid(ps, PkeyContainerId)?.ToString("B") ?? "(null)";
                                nm = ReadString(ps, PkeyFriendlyName) ?? "(null)";
                            }
                            finally { Release(ps); }
                            if (!nm.Contains("耳机") && !nm.Contains("Head")) continue;
                            lines.Add($"  ep {nm}  state={(ep.GetState(out uint st) >= 0 ? st : -1)}  container={cid}  inIndex={index.ContainsKey(cid)}");
                        }
                        finally { Release(ep); }
                    }
                }
            }
            finally { Release(enumerator); }

            var devices = Task.Run(EnumerateAsync).GetAwaiter().GetResult();
            lines.Add($"可用输出设备={devices.Count}");
            foreach (var d in devices)
                lines.Add($"  {d.Name} connected={d.IsConnected} control={d.CanControl} ep={d.EndpointId}");
        }
        catch (Exception ex)
        {
            lines.Add($"异常: {ex.GetType().Name}: {ex.Message}");
        }
        return lines;
    }

    // ---------- COM 基础 ----------

    private static IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator, throwOnError: true)!;
        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    private static Guid? ReadGuid(IPropertyStore store, PropertyKey key)
    {
        var variant = new PropVariant();
        try
        {
            if (store.GetValue(ref key, ref variant) < 0) return null;
            if (variant.VarType != 0x0048 /* VT_CLSID */) return null;
            // VT_CLSID 存的是指针，直接读内联值会拿到堆地址。
            return variant.PointerValue == IntPtr.Zero
                ? null
                : Marshal.PtrToStructure<Guid>(variant.PointerValue);
        }
        finally { PropVariantClear(ref variant); }
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        var variant = new PropVariant();
        try
        {
            if (store.GetValue(ref key, ref variant) < 0) return null;
            if (variant.VarType != 0x001F /* VT_LPWSTR */) return null;
            return Marshal.PtrToStringUni(variant.PointerValue);
        }
        finally { PropVariantClear(ref variant); }
    }

    private static void Release(object? comObject)
    {
        try
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
                Marshal.ReleaseComObject(comObject);
        }
        catch { /* 释放失败不影响功能 */ }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    // ---------- 结构体 ----------

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int Pid;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct KsProperty
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    // PROPVARIANT 在 x64 上是 24 字节（类型 2 + 填充 + 联合体 16）。
    // 尺寸少写会让系统往栈上多写 8 字节，直接 AccessViolation。
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }

    // ---------- COM 接口（顺序必须与 vtable 一致） ----------

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(in Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore? properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector? connector);
        [PreserveSig] int GetSubunitCount(out uint count);
        [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);
        [PreserveSig] int GetPartById(uint id, out IPart? part);
        [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
        [PreserveSig] int GetSignalPath(IPart input, IPart output, bool rejectMixed, out IntPtr signalPath);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int connectorType);
        [PreserveSig] int GetDataFlow(out int dataFlow);
        [PreserveSig] int ConnectTo(IConnector? connectTo);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out bool isConnected);
        [PreserveSig] int GetConnectedTo(out IConnector? connectedTo);
        [PreserveSig] int GetConnectorId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("6DDD10A9-6F19-4C4E-8B4B-0E0F6B7E2E5A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPartsList
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetPart(uint index, out IPart? part);
    }

    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPart
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetLocalId(out uint id);
        [PreserveSig] int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetPartType(out int partType);
        [PreserveSig] int GetSubType(out Guid subType);
        [PreserveSig] int GetControlInterface(uint index, out IntPtr controlInterface);
        [PreserveSig] int EnumPartsIncoming(out IPartsList? parts);
        [PreserveSig] int EnumPartsOutgoing(out IPartsList? parts);
        [PreserveSig] int GetTopologyObject(out IDeviceTopology? topology);
        [PreserveSig] int Activate(int clsCtx, in Guid iid, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
        [PreserveSig] int RegisterSubtype(in Guid subType, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int UnregisterSubtype(in Guid subType, [MarshalAs(UnmanagedType.LPWStr)] string name);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsProperty property, uint propertyLength,
            IntPtr instance, uint instanceLength, out uint bytesReturned);
        [PreserveSig] int KsMethod(ref KsProperty method, uint methodLength,
            IntPtr instance, uint instanceLength, out uint bytesReturned);
        [PreserveSig] int KsEvent(ref KsProperty evt, uint eventLength,
            IntPtr instance, uint instanceLength, out uint bytesReturned);
    }
}
