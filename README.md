<p align="right"><a href="README_EN.md">English</a> | <b>简体中文</b></p>

<p align="center">
  <img src="Assets/logo@1024.png" width="120" alt="BTAudioSink">
</p>

<h1 align="center">BTAudioSink</h1>

<p align="center">
  <b>把 Windows 电脑变成蓝牙音箱</b><br>
  手机、平板、另一台电脑的音频，直接推到这台电脑上播放。支持多台设备同时连接。
</p>

<p align="center">
  <a href="https://github.com/lonelifox/BTAudioSink/releases/latest"><img src="https://img.shields.io/github/v/release/lonelifox/BTAudioSink?style=flat-square&label=release" alt="Release"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%202004%2B-0078D4?style=flat-square" alt="Platform">
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square" alt=".NET">
  <img src="https://img.shields.io/badge/license-MIT-3DA639?style=flat-square" alt="License">
</p>

<p align="center">
  <img src="Assets/screenshot-light.png" width="49%" alt="浅色主题">
  <img src="Assets/screenshot-dark.png" width="49%" alt="深色主题">
</p>

Windows 其实早就内置了这个能力（`AudioPlaybackConnection`，Win10 2004 起），只是系统设置里没有开关。这个程序就是那个开关——没有后台服务，不写驱动。

## 下载

**[→ 前往 Releases 下载最新版](https://github.com/lonelifox/BTAudioSink/releases/latest)**

| 文件 | 大小 | 适合 |
|---|---|---|
| `BTAudioSink-Setup-<版本>-x64.exe` | 约 54 MB | **推荐。** 双击安装，免管理员权限、不弹 UAC，控制面板可正常卸载 |
| `BTAudioSink-win-x64-standalone.zip` | 约 76 MB | 免安装，解压即用。适合放在 U 盘里带走 |

> **首次运行提示「Windows 已保护你的电脑」？** 点「更多信息」→「仍要运行」。
> 这是 SmartScreen 对未签名程序的默认反应，不是病毒警告。想自行验证：源码可 `dotnet build` 复现，
> 每个 Release 附 `SHA256SUMS.txt` 可比对。为什么不签名：[docs/SIGNING.md](docs/SIGNING.md)。

## 功能

这个项目的重点不是把功能堆满，而是**手感**：动画跟着显示器刷新率走，信息该出现时出现、该收起时收起，界面上一眼能分清每个区域在做什么。

- **动画跟刷新率同步** — 面板滑出逐帧驱动，165Hz 屏就是 165fps；托盘菜单里有总开关，关掉后直接出现、直接消失
- **两栏分区，方向不混** — 上半区是「谁在给我送音频」，下半区是「我在往哪送」。同类工具常把两边混在一个列表里，看一眼分不清方向
- **平滑滚动** — 滚轮不再一格一跳，逐帧缓出
- **多设备同时连接** — 每台设备一条独立链路，系统混音器里合并输出
- **耳机也能管** — 下半区列出已配对的耳机 / 音箱，直接连接、断开，并显示电量；超过一小时没更新的旧值自动置灰
- **Win11 原生外观** — 圆角、Mica 毛玻璃、明暗跟随系统（Win10 自动降级为实色窗口）
- **逐台自动重连** — 独立开关，手动断开的不拽回来
- **中英双语** — 托盘菜单切换，立即生效
- **高 DPI 适配** — PerMonitorV2，换显示器不糊
- **运行日志** — 底栏按钮直接打开 `%APPDATA%\BTAudioSink\log.txt`

## 系统要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10 2004（build 19041）或更高 |
| 运行时 | 不需要。安装包和绿色包都自带 .NET 运行时 |
| 蓝牙适配器 | 需支持 A2DP Sink 角色（绝大多数内置和 USB 适配器都支持） |

圆角与 Mica 需要 Windows 11；Win10 上会自动降级为直角实色窗口，功能不受影响。

## 使用

1. 先在系统设置里把手机和电脑**配对**（程序只能看到已配对的设备，不负责配对）
2. 左键点托盘图标打开面板
3. 点设备右侧的「连接」
4. 手机上播放音频

面板分成两区：**接收**（手机 / 平板往本机送声音）和**输出**（本机往耳机送声音）。每台设备一行，点行可展开详情；输出侧还会显示电量和当前编码。

外观、毛玻璃、开机自启、语言、全部断开都在**右键菜单**里。没有关闭按钮——点面板外面就是收起，真正退出用右键菜单的「退出」。

## 常见问题

**列表是空的？**
确认蓝牙已打开、设备已配对。这个接口只枚举已配对的设备。

**提示 `RequestTimedOut`？**
手机端没响应。解锁手机，在蓝牙设置里点一下本机。

**连上了但没有声音？**
检查系统「音量合成器」里对应设备的音量，以及手机是否真的在播放媒体音频。也可以尝试点击「重连」。

**能当麦克风用吗？**
不能。A2DP 是单向的媒体音频传输，只负责把声音送进电脑。

**多台设备同时连接会互相干扰吗？**
不会。各自独立的蓝牙链路，进系统后混合成一路输出。代价是混音器里分不清哪一声来自哪台设备。

**卸载后自启项还在吗？**
不会。卸载器会清掉 HKCU 的 Run 键。绿色版是手动删除的，记得顺手去
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 把 `BTAudioSink` 那一项删掉。

## 从源码构建

需要 .NET 9 SDK。

```bash
git clone https://github.com/lonelifox/BTAudioSink.git
cd BTAudioSink
dotnet build
dotnet run
```

打包安装器、离屏预览工程、体检脚本、CI 流程这些，见 **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**。

## 已知限制

- 只能接收音频，不能当麦克风
- 部分老蓝牙适配器不支持 Sink 角色，`TryCreateFromId` 会返回 null
- 设备必须先在系统设置里完成配对，程序不做配对
- 断一台设备时，系统可能把同一适配器上的其它设备一起报成断开，程序会自动恢复
- Mica 在极少数显卡驱动上可能不渲染，程序会在异常退出后自动关闭它
- 电量取决于耳机是否上报；系统没拿到就显示为空
- 当前编码（SBC / AAC）需要 SYSTEM 级权限才能读，托盘菜单里勾选「以管理员身份运行」后才显示

## 许可

[MIT](LICENSE)
