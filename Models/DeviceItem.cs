using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BTAudioSink.Models;

/// <summary>
/// 设备在列表里的状态。
///
/// 存枚举而不是存一句话，是为了切换语言时能立刻跟着变：
/// 显示文字由 <c>DeviceItem.StatusText</c> 在取用时现查资源。
/// </summary>
public enum DeviceStatus
{
    /// <summary>未连接。</summary>
    Disconnected,

    /// <summary>已连接。</summary>
    Connected,

    /// <summary>正在连接。</summary>
    Connecting,

    /// <summary>连接失败。</summary>
    ConnectFailed,

    /// <summary>正在重新连接。</summary>
    Reconnecting,

    /// <summary>重连失败。</summary>
    ReconnectFailed,

    /// <summary>蓝牙链路未建立（蓝牙这一层没连上，跟 A2DP 不是一回事）。</summary>
    BluetoothNotConnected,

    /// <summary>设备已被移除（取消配对或拔出）。</summary>
    Removed,

    /// <summary>断开失败。</summary>
    DisconnectFailed,
}

/// <summary>
/// 列表里的一台蓝牙设备。UI 直接绑定这些属性。
/// </summary>
public sealed class DeviceItem : INotifyPropertyChanged
{
    private string _name;
    private bool _isConnected;
    private bool _isBusy;
    private bool _isAutoReconnect;
    private DeviceStatus _status = DeviceStatus.Disconnected;
    private string? _statusOverride;

    /// <summary>
    /// 状态文字的覆盖值。有些状态要带上具体原因（比如断开失败时的错误信息），
    /// 这时覆盖值是已经本地化好的整句话；没有覆盖值就按枚举取通用文案。
    /// </summary>
    private string? _addressText;
    private bool? _linkConnected;

    /// <summary>系统报来的信号强度原值。可能是 0 或 -128 这种「没测到」的哨兵值。</summary>
    private int? _rssiValue;

    /// <summary>详情整体查到了没有。查不到时显示「—」，跟「测到了但值不可信」要分得开。</summary>
    private bool _rssiKnown;
    private int? _classMajor;

    public DeviceItem(string id, string name)
    {
        Id = id;
        _name = name;
    }

    public string Id { get; }

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
            if (SetField(ref _isConnected, value)) OnPropertyChanged(nameof(ActionText));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    /// <summary>这台设备的自动重连开关。持久化由主窗口负责，模型只管暴露状态。</summary>
    public bool IsAutoReconnect
    {
        get => _isAutoReconnect;
        set => SetField(ref _isAutoReconnect, value);
    }

    /// <summary>设备当前状态。</summary>
    public DeviceStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>
    /// 状态文字。切换语言后要能立刻变，所以这里每次现取资源，不缓存。
    /// </summary>
    public string StatusText =>
        _statusOverride ?? Services.Loc.Get($"Status.{_status}");

    /// <summary>
    /// 设置状态。带 <paramref name="overrideText"/> 时显示那句话（调用方负责已本地化），
    /// 否则显示枚举对应的通用文案。
    /// </summary>
    public void SetStatus(DeviceStatus status, string? overrideText = null)
    {
        _statusOverride = overrideText;
        Status = status;

        // 状态没变但覆盖文字变了（比如从「未连接」换成带错误原因的「断开失败」），
        // 上面的 setter 不会通知，这里补一次。
        OnPropertyChanged(nameof(StatusText));
    }

    // ---------- 展开详情（选中设备时才显示） ----------

    /// <summary>蓝牙 MAC。查不到时是「—」，不要编一个假地址出来。</summary>
    public string AddressText
    {
        get => _addressText ?? Services.Loc.Get("Detail.Unknown");
        set => SetField(ref _addressText, value);
    }

    /// <summary>详情整体查不到时调用，地址一并归成「未知」。</summary>
    public void ClearAddress()
    {
        if (_addressText is null) return;
        _addressText = null;
        OnPropertyChanged(nameof(AddressText));
    }

    /// <summary>蓝牙链路是否建立。显示文字现取资源，切换语言时自动跟着变。</summary>
    public bool? LinkConnected
    {
        get => _linkConnected;
        set
        {
            if (SetField(ref _linkConnected, value))
            {
                OnPropertyChanged(nameof(LinkText));
            }
        }
    }

    /// <summary>蓝牙链路状态（和 A2DP 连接状态不是一回事）。</summary>
    public string LinkText => _linkConnected switch
    {
        true => Services.Loc.Get("Detail.Linked"),
        false => Services.Loc.Get("Detail.NotLinked"),
        _ => Services.Loc.Get("Detail.Unknown"),
    };

    /// <summary>
    /// 信号强度。显示文字现算：只有落在合理区间（负数、且强于 -121）才当真值显示，
    /// 否则显示「未测到」——系统没测到就老实说没测到，别拿 0 dBm 冒充「信号强」。
    /// </summary>
    public string RssiText
    {
        get
        {
            if (!_rssiKnown) return Services.Loc.Get("Detail.Unknown");
            return _rssiValue is { } value && value > -121 && value < 0
                ? $"{value} dBm"
                : Services.Loc.Get("Detail.RssiUnknown");
        }
    }

    /// <summary>写入系统报来的信号强度。</summary>
    public void SetRssi(int value)
    {
        _rssiValue = value;
        _rssiKnown = true;
        OnPropertyChanged(nameof(RssiText));
    }

    /// <summary>详情整体查不到时调用，信号强度一并归成「未知」。</summary>
    public void ClearRssi()
    {
        _rssiValue = null;
        _rssiKnown = false;
        OnPropertyChanged(nameof(RssiText));
    }

    /// <summary>蓝牙设备的主类别码。显示文字按码查表，切换语言时自动跟着变。</summary>
    public int? ClassMajor
    {
        get => _classMajor;
        set
        {
            if (SetField(ref _classMajor, value))
            {
                OnPropertyChanged(nameof(DeviceTypeText));
            }
        }
    }

    /// <summary>设备类别，取自蓝牙 Class of Device 的主类别码。</summary>
    public string DeviceTypeText => _classMajor is { } code && code is >= 0 and <= 9
        ? Services.Loc.Get($"Class.{code}")
        : Services.Loc.Get("Class.Unknown");

    /// <summary>行末那个按钮的文字：已连接时是「断开」，否则是「连接」。</summary>
    public string ActionText => Services.Loc.Get(
        IsConnected ? "Ui.Device.Disconnect" : "Ui.Device.Connect");

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

    /// <summary>
    /// 语言切换后刷新所有「现取资源」的显示属性。
    /// 这些属性的值依赖当前语言，资源换了得重新通知一次界面。
    /// </summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(LinkText));
        OnPropertyChanged(nameof(DeviceTypeText));
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(RssiText));
    }
}
