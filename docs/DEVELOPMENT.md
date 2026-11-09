# 开发说明

面向要改这个程序的人。只想用的话看 [README](../README.md) 就够了。

## 为什么系统里没有这个开关

蓝牙音频里有两个角色：**发送方（Source）** 和 **接收方（Sink）**。平时电脑连耳机，电脑是发送方；这个程序让电脑反过来当接收方，也就是变成一台「蓝牙音箱」。

从 Windows 10 2004 开始，系统里就有 `Windows.Media.Audio.AudioPlaybackConnection` 这个接口。微软官方文档写得很直接：

> 基础蓝牙 A2DP 接收器的启用**由负责最终用户方案的应用管理，而不是由系统管理**。

功能做好了两三年，就是没放进设置面板——所以有了这个程序。

## 环境

- .NET 9 SDK
- 打包安装程序需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)（CI 上自带，不用装）

```bash
git clone https://github.com/lonelifox/BTAudioSink.git
cd BTAudioSink
dotnet build
dotnet run
```

发布成可以直接分发的目录：

```bash
# 依赖系统 .NET 运行时
dotnet publish -c Release -r win-x64 --self-contained false -o publish

# 自带运行时，对方不用装 .NET
dotnet publish -c Release -r win-x64 --self-contained true -o publish-standalone
```

## 打包安装程序

```bash
# 1. 产出安装器要装的内容（自包含，用户装完就能跑）
dotnet publish BTAudioSink.csproj -c Release -r win-x64 --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false -o publish-installer

# 2. 编译安装程序
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\BTAudioSink.iss
```

产物在 `installer/output/BTAudioSink-Setup-<版本>-x64.exe`。

安装脚本是 `installer/BTAudioSink.iss`，**中文界面是手写的**（`installer/CustomMessages.chinese.isl`）——Inno Setup 官方没提供中文包，翻译放在仓库里是为了 CI 和本地编译结果一致，不依赖外部下载。加新文案时两个文件都要改。

改脚本前先看清这几个设计取舍：

- **按用户安装**，装到 `%LOCALAPPDATA%\Programs`，不弹 UAC。程序自启走 HKCU、数据在 `%APPDATA%`、单实例互斥体也在 `Local\` 命名空间，本来就是单用户的，给「为所有用户安装」的选项没意义。
- **卸载时用 `--exit` 命令行参数**让程序走正常退出流程，不按进程名强杀。强杀会跳过 MicaGuard 的标记清理，下次启动会误判成「上次异常退出」而自动关掉毛玻璃。`--exit` 的退出码如实反映结果（0 已退干净，2 等待超时）。
- **开机自启只在用户勾选后才写注册表**，卸载时无条件清掉——否则注册表会留下指向已删文件的死路径。

## 实现要点

核心只有三步调用：

```csharp
// 1. 只枚举以「音频接收端」身份存在的设备
var watcher = DeviceInformation.CreateWatcher(AudioPlaybackConnection.GetDeviceSelector());

// 2. 为某台设备创建连接对象，null 表示该设备不支持
var connection = AudioPlaybackConnection.TryCreateFromId(deviceId);

// 3. 先报名，再开门
await connection.StartAsync();   // 告诉系统「允许这台设备送音频进来」
await connection.OpenAsync();    // 真正建立连接，音频开始流转
```

`GetDeviceSelector()` 内部按 A2DP AudioSink 服务 UUID（`0000110A-0000-1000-8000-00805F9B34FB`）筛选，所以只能看到支持接收角色的已配对设备。

界面层是 WPF，几个和 Windows 11 对齐的细节：

- 圆角用 `DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND)`，不是自己画 Border
- Mica 需要 `DWMWA_SYSTEMBACKDROP_TYPE` 配合 `WindowChrome.GlassFrameThickness = -1`，否则 DWM 没有地方铺材质
- 无边框窗口最大化时 `WindowChrome` 会溢出工作区，需要按 `rcWork` 手动 `SetWindowPos` 拉回来
- 「自动」外观靠 `SystemEvents.UserPreferenceChanged` 感知系统主题变化；手动选浅色/深色时忽略该事件
- 图标在 16~32px 用**原生像素描线**而不是缩小大图：蓝牙符号有 5 条线，16px 里做平滑缩放必然糊成白斑

## 项目结构

```
BTAudioSink/
├─ App.xaml(.cs)             应用入口、单实例、主题、语言、托盘
├─ MainWindow.xaml(.cs)      主界面
├─ app.manifest              高 DPI（PerMonitorV2）与兼容性声明
├─ Interop/
│  └─ DwmHelper.cs           圆角、Mica、暗色模式
├─ Behaviors/
│  ├─ RevealBehavior.cs      展开/收起动画（改 MaxHeight，不改 Height）
│  └─ RowClickToggle.cs      点行展开、再点收起
├─ Models/DeviceItem.cs      设备列表项
├─ Services/
│  ├─ AudioSinkService.cs    WinRT 连接管理
│  ├─ BluetoothLink.cs       蓝牙链路与设备详情查询
│  ├─ AppLog.cs              文件日志
│  ├─ AppSettings.cs         JSON 配置
│  ├─ AppTheme.cs            外观枚举（自动/浅色/深色）
│  ├─ FlyoutPlacement.cs     弹窗定位：按任务栏方位算位置和滑入方向
│  ├─ StartupManager.cs      开机自启
│  └─ MicaGuard.cs           材质失效时的安全网
├─ Theme/                    明暗主题与控件样式
├─ Assets/                   应用图标与截图
├─ _ui_preview/              离屏预览工程（出图 + 数值断言）
└─ tools/
   ├─ make_icon.py           图标生成脚本（改配色或形状后重新跑）
   ├─ check_loc.py           文案表体检（中英一一对应）
   └─ check_preview_sync.py  预览工程与主界面同步体检
```

## 离屏预览工程

`_ui_preview/` 是一个独立的 WPF 工程，把主界面离屏渲染成图片，并输出可核对的数值断言（对齐情况、列表滚动、底栏高度、文案截断）。

```bash
# 出图 + 断言（日志在 bin/.../frames/log.txt）
dotnet run --project _ui_preview -c Release

# 只重新生成 README 用的两张截图
dotnet run --project _ui_preview -c Release -- --readme --out Assets
```

界面每改一次，README 上的截图就会过时——GitHub 首页展示旧图比没有图更糟，所以改完界面就跑一遍第二条命令。

## 两个体检脚本

改完界面或文案后跑一下，两个都是机器判定，不用人眼比：

```bash
python tools/check_loc.py .          # 中英文案表 + XAML/代码引用的键
python tools/check_preview_sync.py . # 预览工程与主界面是否同步
```

- `check_loc.py`：中英两张表必须键数相同、占位符一致；XAML 里 `{DynamicResource}` 和代码里 `Loc.Get/Format` 引用的键必须都存在；`DeviceStatus` 枚举的每个成员、蓝牙类别码 0-9 都必须有对应的中英文案。**漏一条英文翻译，英文界面就会直接显示键名**——这个坑要在提交时就挡住。
- `check_preview_sync.py`：`_ui_preview/PreviewWindow.xaml` 是主界面的**手工复制品**（预览只渲染有设备的场景，没法直接复用主工程的 XAML）。两边不同步，离屏量出来的宽度就是假的。脚本比对控件类型序列、资源引用、关键布局数值和结构标记，双向检查。

两个脚本在 CI 里都会跑。

> 注意：脚本开头有 `sys.stdout.reconfigure(encoding="utf-8")`。
> GitHub 的 windows runner 控制台是 cp1252，`print` 中文会抛 `UnicodeEncodeError`
> 把整个构建判失败——本机是 UTF-8，这个坑本地永远看不出来。

## CI

`.github/workflows/build.yml`，push 到 main 和打 tag 都跑：

1. 还原 + 构建 + 两个体检脚本 + 预览工程编译
2. 三次 publish（精简 / 绿色 / 安装器负载）
3. 编译安装器
4. 打 tag 时额外建 Release 并上传三个附件，正文取 `RELEASE_NOTES_<tag>.md`

**发版流程**：准备好 `RELEASE_NOTES_vX.Y.Z.md` → 提交 → 打注解 tag `vX.Y.Z` 推上去 → CI 自己构建并发布。
