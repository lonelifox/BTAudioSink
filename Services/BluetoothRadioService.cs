using System;
using System.Runtime.InteropServices;

namespace BTAudioSink.Services;

/// <summary>
/// 通过 Win32 蓝牙 API 启用/停用某台设备上的服务。
///
/// <para>
/// 用途：设备**未连接**时，KS 属性通道还不存在（音频过滤器要等设备起来才创建），
/// 这时只能请蓝牙栈重新加载该设备的音频服务——<c>BluetoothSetServiceState</c> 正是
/// 系统设置里「连接」按钮背后的那套机制之一，属于公开文档的 Win32 API
/// （<c>bluetoothapis.h</c> / <c>bthprops.cpl</c>）。
/// </para>
/// <para>
/// 注意语义：它改的是「该设备的这个服务启用与否」，是持久状态。所以
/// **只在连接时用 ENABLE，不要在断开时用 DISABLE**——断开走 KS oneshot，
/// 那条路不改配置。
/// </para>
/// </summary>
internal static class BluetoothRadioService
{
    public static readonly Guid A2dpSinkService = new("0000110B-0000-1000-8000-00805F9B34FB");
    public static readonly Guid HandsfreeService = new("0000111E-0000-1000-8000-00805F9B34FB");

    private const uint BluetoothServiceEnable = 0x00000001;

    /// <summary>让蓝牙栈重新加载该设备的音频服务，通常会触发一次链路建立。</summary>
    public static bool EnableAudioServices(string mac, out string error)
    {
        error = string.Empty;
        ulong address = ParseMac(mac);
        if (address == 0) { error = "地址无效"; return false; }

        var radioParams = new BluetoothFindRadioParams { Size = Marshal.SizeOf<BluetoothFindRadioParams>() };
        IntPtr radioFind = BluetoothFindFirstRadio(ref radioParams, out IntPtr radio);
        if (radioFind == IntPtr.Zero || radio == IntPtr.Zero)
        {
            error = $"找不到蓝牙适配器（{Marshal.GetLastWin32Error()}）";
            return false;
        }

        try
        {
            var search = new BluetoothDeviceSearchParams
            {
                Size = Marshal.SizeOf<BluetoothDeviceSearchParams>(),
                ReturnAuthenticated = true,
                ReturnRemembered = true,
                ReturnUnknown = false,
                ReturnConnected = true,
                IssueInquiry = false,
            };
            var info = new BluetoothDeviceInfo { Size = Marshal.SizeOf<BluetoothDeviceInfo>() };
            IntPtr deviceFind = BluetoothFindFirstDevice(ref search, ref info);
            if (deviceFind == IntPtr.Zero) { error = "找不到已配对设备"; return false; }

            try
            {
                bool found = false;
                while (true)
                {
                    if (info.Address == address) { found = true; break; }
                    info = new BluetoothDeviceInfo { Size = Marshal.SizeOf<BluetoothDeviceInfo>() };
                    if (!BluetoothFindNextDevice(deviceFind, ref info)) break;
                }
                if (!found) { error = "目标设备不在已配对列表里"; return false; }

                Guid a2dp = A2dpSinkService;
                uint rc = BluetoothSetServiceState(radio, ref info, ref a2dp, BluetoothServiceEnable);
                Guid hfp = HandsfreeService;
                uint rc2 = BluetoothSetServiceState(radio, ref info, ref hfp, BluetoothServiceEnable);
                if (rc != 0 && rc2 != 0)
                {
                    error = $"启用音频服务失败（A2DP={rc}, HFP={rc2}）";
                    return false;
                }
                return true;
            }
            finally
            {
                BluetoothFindDeviceClose(deviceFind);
            }
        }
        finally
        {
            BluetoothFindRadioClose(radioFind);
            CloseHandle(radio);
        }
    }

    private static ulong ParseMac(string mac)
    {
        string hex = mac.Replace(":", string.Empty).Replace("-", string.Empty).Trim();
        return hex.Length == 12 && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out ulong value)
            ? value
            : 0;
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothFindRadioParams
    {
        public int Size;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceSearchParams
    {
        public int Size;
        [MarshalAs(UnmanagedType.Bool)] public bool ReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool ReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool ReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool ReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public int Size;
        public ulong Address;
        public uint ClassOfDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool Connected;
        [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
        [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastSeen;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BluetoothFindRadioParams parameters, out IntPtr radio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BluetoothDeviceSearchParams parameters,
        ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(IntPtr find, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr find);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothSetServiceState(IntPtr radio, ref BluetoothDeviceInfo info,
        ref Guid serviceGuid, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
