; Build with build-installer.ps1 so the application and installer versions agree.
#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1.
#endif
#ifndef PublishDir
  #error PublishDir must point to the validated self-contained publish directory.
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir must be supplied by build-installer.ps1.
#endif

[Setup]
; Keep this ID unchanged across upgrades.
AppId={{38E03914-6824-47B5-AB1A-BDB03661E1A2}
AppName=ResourceStatus
AppVersion={#AppVersion}
AppPublisher=osias10
AppPublisherURL=https://github.com/osias10/windget-resource
AppSupportURL=https://github.com/osias10/windget-resource/issues
AppUpdatesURL=https://github.com/osias10/windget-resource/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=ResourceStatus Setup
DefaultDirName={localappdata}\Programs\ResourceStatus
DefaultGroupName=ResourceStatus
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22621
AppMutex=Local\ResourceStatus.Widget
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\ResourceStatus.exe
OutputDir={#InstallerOutputDir}
OutputBaseFilename=ResourceStatus-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes

[Messages]
SetupAppRunningError=%1 is running. Choose Exit from its system tray menu, then click OK to continue, or Cancel to exit Setup. Closing the widget only hides it.
UninstallAppRunningError=%1 is running. Choose Exit from its system tray menu, then click OK to continue, or Cancel to exit Uninstall. Closing the widget only hides it.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ResourceStatus"; Filename: "{app}\ResourceStatus.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\ResourceStatus"; Filename: "{app}\ResourceStatus.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ResourceStatus.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,ResourceStatus}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RunCommand: String;
  InstalledCommand: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { The app owns this optional startup value. Remove it only if it still
      targets this installation; a portable copy may have replaced it. }
    InstalledCommand := '"' + ExpandConstant('{app}\ResourceStatus.exe') + '"';
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'ResourceStatus', RunCommand) then
    begin
      if CompareText(Trim(RunCommand), InstalledCommand) = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
          'ResourceStatus');
    end;
  end;
  { Deliberately preserve %LocalAppData%\ResourceStatus and its preferences. }
end;
