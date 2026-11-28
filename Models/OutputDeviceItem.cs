using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using BTAudioSink.Services;

namespace BTAudioSink.Models;

/// <summary>输出设备的连接状态。</summary>
public enum OutputStatus
{
    Disconnected,
    Connected,
    Connecting,
    Disconnecting,
    Failed,
}

/// <summary>
/// 面板里「输出设备」那一张卡片。
///
/// 与 <see cref="DeviceItem"/> 分开建模是有意的：来源设备（手机往本机送音频）和
/// 输出设备（本机往耳机送音频）状态机完全不同，硬塞进一个类会让两边都变脏。
/// </summary>
public sealed class OutputDeviceItem : INotifyPropertyChanged
{
    private string _name;
    private string _address;
    private string _endpointId;
    private bool _isConnected;
    private bool _isBusy;
    private bool _canControl;
    private IReadOnlyList<string> _profiles = new List<string>();
    private OutputStatus _status = OutputStatus.Disconnected;
    private int? _battery;
    private string? _codec;
    private bool _batteryStale;

    public OutputDeviceItem(BluetoothOutputService.OutputDevice device)
    {
        ContainerId = device.ContainerId;
        _name = device.Name;
        _address = device.Address;
        _endpointId = device.EndpointId;
        _isConnected = device.IsConnected;
        _canControl = device.CanControl;
        _profiles = device.Profiles;
        _status = device.IsConnected ? OutputStatus.Connected : OutputStatus.Disconnected;
        _battery = device.BatteryPercent;
        _batteryStale = device.BatteryStale;
        _codec = device.Codec;
    }

    public string ContainerId { get; }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (!SetField(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(ActionText));
            OnPropertyChanged(nameof(DotBrushKey));
            if (!_isBusy) _status = value ? OutputStatus.Connected : OutputStatus.Disconnected;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanPress));
        }
    }

    /// <summary>按钮可用性：忙的时候只挡住它自己，不影响别的卡片。</summary>
    public bool CanPress => !_isBusy;

    public bool CanControl
    {
        get => _canControl;
        set
        {
            if (SetField(ref _canControl, value)) OnPropertyChanged(nameof(ControlText));
        }
    }

    public IReadOnlyList<string> Profiles
    {
        get => _profiles;
        set
        {
            _profiles = value;
            OnPropertyChanged(nameof(ProfileText));
        }
    }

    public string EndpointId => _endpointId;

    public OutputStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => Loc.Get($"Status.Out{_status}");

    public string ActionText => Loc.Get(IsConnected ? "Ui.Output.Disconnect" : "Ui.Output.Connect");

    /// <summary>电量百分比。系统没报电量时为 null，界面不显示。</summary>
    public bool HasBattery => _battery is int;

    public bool HasCodec => !string.IsNullOrEmpty(_codec);

    /// <summary>电量与编码至少要有一项，整块信息才显示。</summary>
    public bool HasInfo => HasBattery || HasCodec;

    public string BatteryText => _battery is int value ? $"{value}%" : string.Empty;

    /// <summary>电量条宽度（DIP）。外壳 19 宽，扣掉两侧留白与右极凸起，满格约 13。</summary>
    public double BatteryFillWidth => _battery is int value
        ? Math.Max(2.0, 13.0 * Math.Clamp(value, 0, 100) / 100.0)
        : 0.0;

    /// <summary>20% 以下转红，跟系统对低电量的处理一致。</summary>
    public bool IsBatteryLow => _battery is int value && value <= 20;

    /// <summary>超过一小时没更新过的旧值：整块置灰，避免被当成实时电量。</summary>
    public bool IsBatteryStale => _batteryStale;

    /// <summary>
    /// Segoe MDL2 的电池字形：E850（空）到 E85A（满），共 11 档。
    /// 按 10% 一档取，跟系统音量/电量图标的颗粒度一致。
    /// </summary>
    public string BatteryGlyph
    {
        get
        {
            if (_battery is not int value) return string.Empty;
            int level = Math.Clamp((int)Math.Round(value / 10.0), 0, 10);
            return char.ConvertFromUtf32(0xE850 + level);
        }
    }

    public string CodecText => _codec ?? string.Empty;

    public string AddressText => _address.Length > 0 ? _address : Loc.Get("Detail.Unknown");

    /// <summary>卡片左侧圆点的颜色键：已连接用成功色，其余用次要色。</summary>
    public string DotBrushKey => IsConnected ? "SuccessBrush" : "TextTertiaryBrush";

    public string ProfileText => _profiles.Count == 0
        ? Loc.Get("Detail.Unknown")
        : string.Join(" / ", _profiles.Distinct());

    public string ControlText => _canControl ? Loc.Get("Detail.Available") : Loc.Get("Detail.Unavailable");

    public string IdText => ContainerId.Length > 8 ? ContainerId[..8] : ContainerId;

    /// <summary>用最新快照更新自己；返回是否发生了变化。</summary>
    public bool Update(BluetoothOutputService.OutputDevice device)
    {
        bool changed = _name != device.Name || _isConnected != device.IsConnected
                       || _address != device.Address || _canControl != device.CanControl
                       || !_profiles.SequenceEqual(device.Profiles);

        _endpointId = device.EndpointId;
        Name = device.Name;
        _address = device.Address;
        _profiles = device.Profiles;
        _battery = device.BatteryPercent;
        _batteryStale = device.BatteryStale;
        _codec = device.Codec;
        OnPropertyChanged(nameof(HasBattery));
        OnPropertyChanged(nameof(IsBatteryStale));
        OnPropertyChanged(nameof(HasCodec));
        OnPropertyChanged(nameof(HasInfo));
        OnPropertyChanged(nameof(BatteryText));
        OnPropertyChanged(nameof(BatteryGlyph));
        OnPropertyChanged(nameof(BatteryFillWidth));
        OnPropertyChanged(nameof(IsBatteryLow));
        OnPropertyChanged(nameof(CodecText));
        CanControl = device.CanControl;
        IsConnected = device.IsConnected;
        if (!_isBusy) Status = device.IsConnected ? OutputStatus.Connected : OutputStatus.Disconnected;

        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(ProfileText));
        return changed;
    }

    /// <summary>语言切换后刷新所有现取资源的显示属性。</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(ProfileText));
        OnPropertyChanged(nameof(ControlText));
        OnPropertyChanged(nameof(AddressText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
