; Inno Setup script for the PlaylistMixer Companion.
; Installs a Windows service (auto-start) that proxies + remuxes streams on the user's own machine,
; plus a system-tray app (autostart at login) to control it. Bundles ffmpeg.exe.
;
; Build via build.ps1 (publishes the exes into .\stage\app first), or:
;   ISCC /DMyAppVersion=1.0.0 PlaylistMixerCompanion.iss
;
; v1 may ship UNSIGNED (users may see a one-time SmartScreen prompt). For release, sign both the
; published exes and the resulting installer with an OV/EV code-signing certificate.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "PlaylistMixer Companion"
#define MyServiceName "PlaylistMixerCompanion"
#define MyServiceExe "PlaylistMixer.Companion.Service.exe"
#define MyTrayExe "PlaylistMixer.Companion.Tray.exe"

[Setup]
AppId={{6F3A2C18-7B4E-4D2A-9C2F-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=PlaylistMixer
DefaultDirName={autopf}\PlaylistMixer Companion
DefaultGroupName=PlaylistMixer Companion
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
OutputDir=output
OutputBaseFilename=PlaylistMixer-Companion-Setup
; Brand the setup wizard and Add/Remove Programs entry with the app logo. The uninstall icon reuses
; the tray exe's embedded icon (ApplicationIcon in the Tray .csproj) so no extra .ico is shipped.
SetupIconFile=..\PlaylistMixer.Companion.Tray\brand.ico
UninstallDisplayIcon={app}\{#MyTrayExe}
Compression=lzma2
SolidCompression=yes
; The service must be registered and the tray autostart written for all users.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern

[Files]
Source: "stage\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Registry]
; Tray autostart at login (all users). Tray runs unelevated (asInvoker).
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "PlaylistMixerCompanionTray"; ValueData: """{app}\{#MyTrayExe}"""; Flags: uninsdeletevalue

[Run]
; Register the service — only when it doesn't already exist (in-place upgrades keep the existing
; registration; PrepareToInstall stopped it so its exe could be replaced, and the start below
; relaunches the new binary).
Filename: "{sys}\sc.exe"; Parameters: "create {#MyServiceName} binPath= ""{app}\{#MyServiceExe}"" start= auto DisplayName= ""{#MyAppName}"""; \
  Flags: runhidden; StatusMsg: "Registering the companion service..."; Check: ServiceMissing
Filename: "{sys}\sc.exe"; Parameters: "description {#MyServiceName} ""Plays PlaylistMixer streams locally so the server uses no bandwidth or CPU for playback."""; \
  Flags: runhidden
; Auto-restart the service if it ever crashes.
Filename: "{sys}\sc.exe"; Parameters: "failure {#MyServiceName} reset= 60 actions= restart/5000/restart/5000/restart/5000"; \
  Flags: runhidden
; Grant Authenticated Users start/stop (RP/WP) so the unelevated tray can control it without UAC.
Filename: "{sys}\sc.exe"; Parameters: "sdset {#MyServiceName} ""D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)(A;;RPWP;;;AU)S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)"""; \
  Flags: runhidden
; Start the service and launch the tray now.
Filename: "{sys}\sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden
Filename: "{app}\{#MyTrayExe}"; Description: "Start the tray now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Stop + remove the service and the running tray before files are deleted.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyTrayExe} /F"; Flags: runhidden; RunOnceId: "KillTray"
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden; RunOnceId: "DelSvc"

[Code]
{ In-place upgrade support. When re-running the installer over an existing install, the service's
  exe is locked (it's running) and "sc create" would fail because the service already exists. So:
  - PrepareToInstall (runs before files are copied) stops the tray + service and waits for the
    service process to actually exit, releasing its exe for replacement.
  - The [Run] "sc create" entry is gated on ServiceMissing, so upgrades keep the existing
    registration and just restart the freshly-copied binary; fresh installs still register it. }

{ Returns the service's textual state ('STOPPED'/'RUNNING'/'OTHER'); sets Found=False if absent. }
function ServiceState(var Found: Boolean): String;
var
  rc, i: Integer;
  outp: TExecOutput;
  line: String;
begin
  Result := '';
  Found := False;
  if not ExecAndCaptureOutput(ExpandConstant('{sys}\sc.exe'), 'query {#MyServiceName}', '',
    SW_HIDE, ewWaitUntilTerminated, rc, outp) then
    Exit;
  if rc <> 0 then
    Exit; { 1060 = the service does not exist }
  Found := True;
  for i := 0 to GetArrayLength(outp.StdOut) - 1 do
  begin
    line := outp.StdOut[i];
    if Pos('STATE', line) > 0 then
    begin
      if Pos('STOPPED', line) > 0 then Result := 'STOPPED'
      else if Pos('RUNNING', line) > 0 then Result := 'RUNNING'
      else Result := 'OTHER';
      Exit;
    end;
  end;
end;

{ Check function for the [Run] "sc create" entry: true only when the service isn't registered. }
function ServiceMissing: Boolean;
var
  Found: Boolean;
begin
  ServiceState(Found);
  Result := not Found;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  rc, i: Integer;
  Found: Boolean;
  State: String;
begin
  Result := '';
  { Kill the tray so its exe unlocks (tray runs in the user session; the elevated installer can end it). }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#MyTrayExe} /F', '', SW_HIDE, ewWaitUntilTerminated, rc);
  State := ServiceState(Found);
  if not Found then
    Exit; { fresh install — nothing to stop }
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  { Wait up to ~20s for the service to actually stop so its process exits and releases the exe. }
  for i := 1 to 40 do
  begin
    State := ServiceState(Found);
    if (not Found) or (State = 'STOPPED') then
      Break;
    Sleep(500);
  end;
  Sleep(1000); { brief grace for file handles to close before [Files] runs }
end;
