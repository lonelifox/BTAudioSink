using System;
using System.Runtime.InteropServices;

namespace BTAudioSink.Services;

/// <summary>
/// 把某个音频端点设为系统默认输出。
///
/// <para>
/// 用途：蓝牙耳机断着的时候，KS 属性通道还不存在。把默认输出切到它，
/// 音频服务会去**激活**这个端点，Windows 随之建立 A2DP 链路——等同于用户在
/// 声音设置里点选它。相比改蓝牙服务状态，这条路只动「默认设备」，
/// 不碰配对与配置文件。
/// </para>
/// <para>
/// 注意 <c>IPolicyConfig</c> 是系统内部接口（没有公开头文件），
/// 这里按公开的 vtable 布局声明，只调用 <c>SetDefaultEndpoint</c> 一个方法。
/// 因此失败时要有兜底，不能把它当成唯一路径。
/// </para>
/// </summary>
internal static class AudioEndpointPolicy
{
    private static readonly Guid ClsidPolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid IidPolicyConfig = new("F8679F50-850A-41CF-9C72-430F290290C8");

    /// <summary>ERole：0 控制台 / 1 多媒体 / 2 通信。媒体播放看的是 eMultimedia。</summary>
    public static bool SetDefaultEndpoint(string deviceId, int role, out string error)
    {
        error = string.Empty;
        object? obj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidPolicyConfigClient, throwOnError: true)!;
            obj = Activator.CreateInstance(type);
            if (obj is not IPolicyConfig policy)
            {
                error = "系统未提供 IPolicyConfig 接口";
                return false;
            }

            int hr = policy.SetDefaultEndpoint(deviceId, role);
            if (hr < 0)
            {
                error = $"SetDefaultEndpoint hr=0x{hr:X8}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    /// <summary>按 vtable 顺序声明；未用到的签名只是占位，保证索引不错位。</summary>
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out long period, out long minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
