; BTAudioSink 安装脚本（Inno Setup 6）
;
; 定位：按用户安装，不弹 UAC。
; 这个程序本来就是单用户工具——开机自启写的是 HKCU 注册表（不需要管理员），
; 配置和日志都在 %APPDATA%，exe 同级目录不写任何文件。
; 所以没必要装进 Program Files 要管理员权限，直接装到 %LOCALAPPDATA%\Programs 最省事。
;
; 编译：ISCC.exe BTAudioSink.iss
; 依赖：publish-installer 目录（由 dotnet publish 生成，见 README）

#define AppName "BTAudioSink"
#define AppVersion "1.1.0"
#define AppPublisher "HB"
#define AppExeName "BTAudioSink.exe"
#define AppUrl "https://github.com/lonelifox/BTAudioSink"

[Setup]
AppId={{8C4F2A1B-9D3E-4A7F-B6C2-1E5D8A3F7B29}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}

; 按用户安装：默认目录在用户目录下，不触发 UAC。
; 好处是这程序本来就是单用户的——自启写 HKCU、数据在 %APPDATA%、
; 单实例互斥体也在 Local\ 命名空间（不跨用户会话）。
; 所以不提供「为所有用户安装」的选项：那个对话框对这个程序没有意义，
; 反而给小白用户平白多一道选择题（PrivilegesRequiredOverridesAllowed 不给就是不给）。
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest

; 相对路径都相对脚本所在目录（installer/）解析：
;   publish-installer\ → 项目根下 dotnet publish 出来的目录
;   ..\Assets\app.ico  → 项目根下的图标
OutputDir=output
OutputBaseFilename=BTAudioSink-Setup-{#AppVersion}-x64
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes

; 装完直接可运行，不需要重启
WizardStyle=modern
WizardSizePercent=100

; 版本信息写进 exe，资源管理器属性里能看到
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

; 卸载时如果程序还在跑，先关掉，否则文件删不掉
CloseApplications=yes
RestartApplications=no

[Languages]
; 中文语言包在仓库里（installer/CustomMessages.chinese.isl），不依赖外部下载，
; 保证 CI 和本地编译结果一致。
Name: "chinese"; MessagesFile: "CustomMessages.chinese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; 开机自启默认不勾：这程序的价值是「需要时把声音接过来」，
; 绝大多数用户不会天天开着它占一个托盘位。
; 想常驻的人自己勾，比默认替用户做决定好。
Name: "autostart"; Description: "开机自动启动（后台运行，不弹窗口）"; GroupDescription: "其他选项："; Flags: unchecked

[Files]
; 整个 publish-installer 目录。装进去的 exe 需要自包含运行时，
; 免得用户机器上没装 .NET 就跑不起来。
Source: "..\publish-installer\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 开始菜单：主程序 + 卸载
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"; WorkingDir: "{app}"
; 桌面快捷方式由上面的 Task 控制
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; 这里故意不写开机自启：那个键由用户在安装向导里勾选「开机自动启动」后才写，
; 见 [Code] 的 CurStepChanged。
; 如果在 [Registry] 里无条件写，装完就等于偷偷开了自启，用户没勾也开了。
;
; 但卸载时必须清掉它，否则注册表里会留下指向已删文件的死路径，
; 开机时系统会反复尝试拉起一个不存在的程序。清理逻辑放在 CurUninstallStepChanged。

[Run]
; 装完直接启动
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run';

// 用户勾了「开机自动启动」才写注册表。
// 值里带 --minimized，让程序开机时直接缩到托盘不弹窗口（与程序内部约定一致）。
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('autostart') then
      RegWriteStringValue(HKCU, RunKeyPath, '{#AppName}',
        '"' + ExpandConstant('{app}\{#AppExeName}') + '" --minimized');
  end;
end;

// 卸载时清掉开机自启。
// 不清的话注册表里会留一个指向已删除 exe 的路径，开机时系统会一直尝试拉起它。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, RunKeyPath, '{#AppName}');
end;

// 卸载时如果程序还在托盘里跑，先请它退出，否则 exe 被占用删不掉。
// 这个程序是托盘常驻的：窗口可能早就收起了，Restart Manager 找不到窗口句柄就关不掉它，
// 结果就是卸载时一堆文件删不掉（错误 145/32）。
//
// 用程序自己的 --exit 参数：它会通知运行中的实例走正常退出流程
// （清 MicaGuard 标记、断开蓝牙、写日志），并等它真的退出才返回。
// 比按进程名强杀干净得多——强杀会跳过标记清理，下次启动会误判成异常退出。
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  // ewWaitUntilTerminated：等 --exit 那个进程自己结束，
  // 它退出的那一刻，运行中的实例也已经把文件释放了。
  if not Exec(ExpandConstant('{app}\{#AppExeName}'), '--exit', '', SW_HIDE,
              ewWaitUntilTerminated, ResultCode) then
  begin
    // 拉不起来（文件已被删或损坏）就当没在跑，直接卸。
    Result := True;
  end;
end;
