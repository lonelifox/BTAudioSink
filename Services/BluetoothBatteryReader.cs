using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BTAudioSink.Services;

/// <summary>
/// 读蓝牙设备的电量。
///
/// <para><b>为什么不能只查主节点</b>：电量挂在设备的某个子节点上（实测是
/// <c>Hands-Free AG</c> 那一个），主节点 <c>BTHENUM\DEV_xxx</c> 上根本没有这个属性。
/// Windows 设置页之所以能显示，是因为它按「同一台物理设备」把节点归并起来再取值。</para>
///
/// <para>这里同样按容器 ID 归并：遍历所有设备节点，取
/// <c>DEVPKEY_Device_ContainerId</c> 和 <c>PKEY_BluetoothBatteryLevel</c>，
/// 得到「容器 → 电量」的映射，再跟音频端点那边的容器对上号。</para>
/// </summary>
internal static class BluetoothBatteryReader
{
    /// <summary>PKEY_BluetoothBatteryLevel，类型 BYTE。</summary>
    private static readonly DevPropKey BatteryKey = new()
    {
        FormatId = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
        Pid = 2,
    };

    private static readonly DevPropKey ContainerKey = new()
    {
        FormatId = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        Pid = 2,
    };

    private const uint DigcfPresent = 0x02;
    private const uint DigcfAllClasses = 0x04;
    private const uint DevPropTypeGuid = 0x0D;
    private const uint DevPropTypeByte = 0x03;

    /// <summary>返回「容器 ID → 电量百分比」。读不到的设备不会出现在结果里。</summary>
    public static Dictionary<string, int> ReadAll()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

        try
        {
            var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                string instanceId = GetInstanceId(set, ref data);
                if (instanceId.Length == 0) continue;

                // 只看蓝牙相关的节点，别的设备读了没意义。
                if (!instanceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase) &&
                    !instanceId.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase) &&
                    !instanceId.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase))
                    continue;

                Guid? container = ReadGuid(set, ref data, ContainerKey);
                if (container is null) continue;

                if (!TryReadByte(set, ref data, BatteryKey, out byte battery)) continue;
                if (battery > 100) continue;

                string key = container.Value.ToString("D");
                // 同一台设备的多个节点都可能带电量，取最大值（AG 节点通常是最新的）。
                if (!result.TryGetValue(key, out int existing) || battery > existing)
                    result[key] = battery;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Battery scan failed: {ex.Message}");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static string GetInstanceId(IntPtr set, ref SpDevInfoData data)
    {
        var buffer = new StringBuilder(512);
        return SetupDiGetDeviceInstanceIdW(set, ref data, buffer, (uint)buffer.Capacity, out _)
            ? buffer.ToString()
            : string.Empty;
    }

    private static Guid? ReadGuid(IntPtr set, ref SpDevInfoData data, DevPropKey key)
    {
        var buffer = new byte[16];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out uint type, buffer, 16, out _, 0))
            return null;
        if (type != DevPropTypeGuid) return null;
        return new Guid(buffer);
    }

    private static bool TryReadByte(IntPtr set, ref SpDevInfoData data, DevPropKey key, out byte value)
    {
        value = 0;
        var buffer = new byte[1];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out uint type, buffer, 1, out _, 0))
            return false;
        if (type != DevPropTypeByte) return false;
        value = buffer[0];
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator,
        IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData, StringBuilder deviceInstanceId, uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData, ref DevPropKey propertyKey, out uint propertyType,
        byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
