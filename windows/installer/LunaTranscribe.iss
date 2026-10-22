; Installer für LunaTranscribe (Windows) – Inno Setup 6.
;
; Wird von scripts\package-release.ps1 gebaut, nicht direkt:
;   ISCC.exe /DAppVersion=1.1.0 /DSourceDir=<publish-Ordner> /DOutputDir=<dist> /DOutputBaseFilename=<name> installer\LunaTranscribe.iss
;
; Installiert pro Benutzer ohne Administratorrechte nach %LOCALAPPDATA%\Programs\LunaTranscribe (wie build-release.ps1),
; legt eine Startmenü-Verknüpfung an und auf Wunsch Desktop-Verknüpfung und Autostart. Ein Update beendet die laufende
; App, ersetzt den Programmordner vollständig und behält Einstellungen, Modelle und Meetings. Die Deinstallation
; entfernt nur den Programmordner, Verknüpfungen und den Autostart-Eintrag – Einstellungen (%APPDATA%\LunaTranscribe),
; Modelle (%LOCALAPPDATA%\LunaTranscribe) und der Meeting-Ordner bleiben erhalten.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir fehlt (publish-Ordner der App)
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "LunaTranscribe-" + AppVersion + "-windows-x64-setup"
#endif

#define AppName "LunaTranscribe"
#define AppExe "LunaTranscribe.exe"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
; Fester Schlüssel: verbindet Updates und Deinstallation über alle Versionen hinweg – nie ändern.
AppId={{BD70161A-3C9C-44DE-B2B4-958AFF5BC916}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Maximilian Hofmeister
AppPublisherURL=https://lunatranscribe.aljoma.at
AppSupportURL=https://github.com/maximilianhofmeister1/LunaTranscribe
AppUpdatesURL=https://github.com/maximilianhofmeister1/LunaTranscribe/releases
AppCopyright=© 2026 Maximilian Hofmeister
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
; Pro Benutzer, ohne Administrator: {autopf} = %LOCALAPPDATA%\Programs
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004 – wie die App (WinRT-Medienwiedergabe)
MinVersion=10.0.19041
SetupIconFile=..\LunaTranscribe\Assets\LunaTranscribe.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Kompression in eigenem (64-bit-)Prozess: der 32-bit-Compiler hat für ~300 MB sonst zu wenig Speicher.
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=2
; Die laufende App beendet [Code] selbst (Tray-App ohne Fenster reagiert nicht auf den Restart Manager).
CloseApplications=no
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
de.AutostartTask=Beim Anmelden automatisch starten
en.AutostartTask=Start automatically when signing in
de.LaunchApp=LunaTranscribe jetzt starten
en.LaunchApp=Launch LunaTranscribe now
de.StartGroup=Start:
en.StartGroup=Start:

[Tasks]
; Wie auf macOS standardmäßig aus; ein Update lässt einen in der App gesetzten Autostart unangetastet.
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:StartGroup}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Update: alten Programmordner vollständig leeren, damit keine veralteten nativen DLLs (llama.cpp, ONNX) übrig bleiben.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Lokale Diktierfunktion (offline)"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Derselbe Eintrag, den die App unter Einstellungen → Allgemein → „Beim Anmelden automatisch starten“ setzt.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
{ Laufende App und luna-mcp beenden, bevor Dateien ersetzt oder entfernt werden. }
procedure StopRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM luna-mcp.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { Autostart-Eintrag entfernen – auch wenn er nicht vom Installer, sondern aus der App heraus gesetzt wurde. }
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', '{#AppName}');
end;
