; TCode 的 Windows 安装程序（Inno Setup 6，docs/design/auto-update.md §4、§7.1）。
; 首次安装走向导；应用内更新以 /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /UPDATE=1 启动：
; 等 TCode 退出（AppMutex 同名的 TCodeDesktop）后覆盖文件，完成后自动重新打开 TCode。
; 按用户安装到 %LocalAppData%\Programs\TCode，不需要管理员权限，所以更新时也不需要提权。
; 编译: iscc /DAppVersion=1.2.0 /DSourceDir=<发布目录> /DOutputDir=<输出目录> TCode.iss

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{6F2A9C1E-4B7D-4E8A-9C3F-2D1B5A7E8F40}
AppName=TCode
AppVersion={#AppVersion}
AppVerName=TCode {#AppVersion}
AppPublisher=TCode
AppPublisherURL=https://github.com/AtomUICommunity/TCode
AppUpdatesURL=https://github.com/AtomUICommunity/TCode/releases
DefaultDirName={localappdata}\Programs\TCode
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=TCode-{#AppVersion}-win-x64-setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\TCode.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 仍有进程占用文件时（例如旧版本的 pty host）由 Restart Manager 关闭它们；不自动重启它们，TCode 由 [Run] 重新打开
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; 子目录整体替换，避免旧版本留下的文件（例如已改名的 dll）残留
Type: filesandordirs; Name: "{app}\agenthost"
Type: filesandordirs; Name: "{app}\ptyhost"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.runtimeconfig.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\TCode"; Filename: "{app}\TCode.exe"
Name: "{autodesktop}\TCode"; Filename: "{app}\TCode.exe"; Tasks: desktopicon

[Run]
; 首次安装：向导最后一页的「运行 TCode」
Filename: "{app}\TCode.exe"; Description: "{cm:LaunchProgram,TCode}"; Flags: nowait postinstall skipifsilent
; 应用内更新（/UPDATE=1）：静默安装完成后重新打开
Filename: "{app}\TCode.exe"; Flags: nowait; Check: IsUpdate

[Code]
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

// TCode 启动安装程序后立即退出；最多等 30 秒，等它（连同 Agent Host）完全退出后再开始替换文件
function InitializeSetup: Boolean;
var
  I: Integer;
begin
  I := 0;
  while CheckForMutexes('TCodeDesktop') and (I < 60) do
  begin
    Sleep(500);
    I := I + 1;
  end;
  Result := not CheckForMutexes('TCodeDesktop');
  if not Result then
    SuppressibleMsgBox('TCode is still running. Close it and run Setup again.', mbError, MB_OK, IDOK);
end;
