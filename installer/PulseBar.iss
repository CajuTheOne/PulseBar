; Установщик PulseBar (Inno Setup 6). Собирает build.ps1 или GitHub Actions:
;   ISCC /DAppVersion=2.0.0 /DSourceDir=<папка dotnet publish> installer\PulseBar.iss
; Ставит строку и службу PulseBarSensor в Program Files, при необходимости .NET 9 Desktop Runtime и PawnIO,
; удаляет PulseBar 1.x (задача «\AI Tools\PulseBar» и папка «C:\Program Files\AI Tools\PulseBar»).
; Обновление из программы запускает этот же установщик с /SILENT — он закрывает и снова запускает PulseBar.

#ifndef AppVersion
  #define AppVersion "2.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#define ServiceName "PulseBarSensor"

[Setup]
AppId={{8D3B6C2E-5F4A-4E1B-9C7D-2A6F0B1E4C93}
AppName=PulseBar
AppVersion={#AppVersion}
AppVerName=PulseBar {#AppVersion}
AppPublisher=CajuTheOne
AppPublisherURL=https://github.com/CajuTheOne/PulseBar
AppSupportURL=https://github.com/CajuTheOne/PulseBar/issues
AppUpdatesURL=https://github.com/CajuTheOne/PulseBar/releases
VersionInfoVersion={#Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1)}
DefaultDirName={autopf}\PulseBar
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=no
OutputDir=..\artifacts\installer
OutputBaseFilename=PulseBar-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\PulseBar.exe
SetupIconFile=..\src\PulseBar\PulseBar.ico
UninstallDisplayName=PulseBar

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[CustomMessages]
en.Autostart=Start PulseBar when I sign in to Windows
ru.Autostart=Запускать PulseBar при входе в Windows
uk.Autostart=Запускати PulseBar під час входу у Windows
en.PawnIO=Install the PawnIO driver (CPU temperature)
ru.PawnIO=Установить драйвер PawnIO (температура CPU)
uk.PawnIO=Встановити драйвер PawnIO (температура CPU)
en.Launch=Start PulseBar
ru.Launch=Запустить PulseBar
uk.Launch=Запустити PulseBar
en.Prereq=Downloading %1…
ru.Prereq=Загрузка %1…
uk.Prereq=Завантаження %1…
en.PrereqFailed=Could not install %1 (code %2).
ru.PrereqFailed=Не удалось установить %1 (код %2).
uk.PrereqFailed=Не вдалося встановити %1 (код %2).

[Tasks]
Name: "autostart"; Description: "{cm:Autostart}"
Name: "pawnio"; Description: "{cm:PawnIO}"; Check: not PawnIOInstalled

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Ярлык в меню «Пуск» (для всех пользователей); если строка уже запущена, он открывает её окно настроек.
Name: "{autoprograms}\PulseBar"; Filename: "{app}\PulseBar.exe"; Comment: "PulseBar"

[Registry]
; Автозапуск строки — без прав администратора, для пользователя, который ставит программу.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PulseBar"; ValueData: """{app}\PulseBar.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\PulseBar.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall runasoriginaluser

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PulseBar.Sensor.exe"; Flags: runhidden waituntilterminated; RunOnceId: "KillService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PulseBar.exe"; Flags: runhidden waituntilterminated; RunOnceId: "KillApp"

[Code]
function PawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

function DotNetDesktop9Installed: Boolean;
var
  F: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\9.*'), F);
  if Result then
    FindClose(F);
end;

function Run(const FileName, Params: String): Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

function Sc(const Params: String): Integer;
begin
  Result := Run(ExpandConstant('{sys}\sc.exe'), Params);
end;

// Скачать и тихо поставить зависимость; пустая строка — успех, иначе текст ошибки.
function InstallPrereq(const Name, Url, FileName, Params: String): String;
var
  Code: Integer;
begin
  Result := '';
  WizardForm.StatusLabel.Caption := FmtMessage(CustomMessage('Prereq'), [Name]);
  try
    DownloadTemporaryFile(Url, FileName, '', nil);
  except
    Result := GetExceptionMessage;
    Exit;
  end;
  Code := Run(ExpandConstant('{tmp}\') + FileName, Params);
  // 0 — готово, 3010 — готово, нужна перезагрузка, 1638 — уже установлена более новая версия.
  if (Code <> 0) and (Code <> 3010) and (Code <> 1638) then
    Result := FmtMessage(CustomMessage('PrereqFailed'), [Name, IntToStr(Code)]);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // Остановить то, что держит файлы: строку (у всех пользователей) и службу.
  Sc('stop {#ServiceName}');
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PulseBar.exe');
  Sleep(1500);
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PulseBar.Sensor.exe');

  // PulseBar 1.x: задача Планировщика с правами администратора и своя папка.
  Run(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "\AI Tools\PulseBar" /F');
  if DirExists(ExpandConstant('{commonpf64}\AI Tools\PulseBar')) then
  begin
    DelTree(ExpandConstant('{commonpf64}\AI Tools\PulseBar'), True, True, True);
    RemoveDir(ExpandConstant('{commonpf64}\AI Tools'));
  end;

  if not DotNetDesktop9Installed then
    Result := InstallPrereq('.NET 9 Desktop Runtime', 'https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe',
      'windowsdesktop-runtime-win-x64.exe', '/install /quiet /norestart');
  if (Result = '') and WizardIsTaskSelected('pawnio') and not PawnIOInstalled then
    Result := InstallPrereq('PawnIO', 'https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe',
      'PawnIO_setup.exe', '-install -silent');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Bin: String;
begin
  if CurStep <> ssPostInstall then
    Exit;
  Bin := 'binPath= "\"' + ExpandConstant('{app}') + '\PulseBar.Sensor.exe\""';
  // 1073 — служба уже есть (обновление): поправить путь.
  if Sc('create {#ServiceName} ' + Bin + ' start= auto DisplayName= "PulseBar Sensor"') = 1073 then
    Sc('config {#ServiceName} ' + Bin + ' start= auto');
  Sc('description {#ServiceName} "PulseBar: CPU temperature for the taskbar bar (PawnIO)"');
  Sc('failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/60000');
  Sc('start {#ServiceName}');
end;
