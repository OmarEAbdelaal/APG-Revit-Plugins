; Inno Setup script for the APG Revit Plugins suite (Code Compliance - Fire Fighting,
; Ramp Creator). Compile with Inno Setup 6 (ISCC.exe) after building the Release
; configurations, or simply run installer\build-installer.ps1 which does both.
;
; The single installer:
;   - ships every plugin of the suite in one .exe
;   - needs NO admin rights (installs per-user under %APPDATA%)
;   - detects which Revit versions (2024-2027) are installed and deploys only to those
;   - includes a normal Windows uninstaller (Settings > Apps)

#define MyAppName "APG Revit Plugins"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "APG - Omar Elsayed"
#define MyAppURL "https://github.com/OmarEAbdelaal/APG-Revit-Plugins"
#define BinRoot "..\src\CodeCompliance\bin"
#define ManifestFile "..\install\CodeCompliance.addin"

[Setup]
AppId={{6319B64E-55D0-4ACA-8F1B-BB4B6748512D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
; Per-user install: no UAC prompt, no admin rights required
PrivilegesRequired=lowest
; {app} only stores the uninstaller; the add-in itself goes to the Revit Addins folders
DefaultDirName={userappdata}\CodeCompliance
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyMemo=no
OutputDir=output
OutputBaseFilename=APG-Revit-Plugins-Setup-{#MyAppVersion}
SetupIconFile=apg.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={uninstallexe}

[Files]
; Each block ships only if that Revit version's build output exists,
; and installs only if that Revit version is detected on the user's machine
; (or no Revit was detected at all, in which case everything is installed).

#if FileExists(BinRoot + "\Release R24\CodeCompliance.dll")
Source: "{#BinRoot}\Release R24\CodeCompliance.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\CodeCompliance"; Flags: ignoreversion; Check: InstallFor('2024')
Source: "{#ManifestFile}"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Flags: ignoreversion; Check: InstallFor('2024')
#endif

#if FileExists(BinRoot + "\Release R25\CodeCompliance.dll")
Source: "{#BinRoot}\Release R25\CodeCompliance.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\CodeCompliance"; Flags: ignoreversion; Check: InstallFor('2025')
Source: "{#ManifestFile}"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Flags: ignoreversion; Check: InstallFor('2025')
#endif

#if FileExists(BinRoot + "\Release R26\CodeCompliance.dll")
Source: "{#BinRoot}\Release R26\CodeCompliance.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\CodeCompliance"; Flags: ignoreversion; Check: InstallFor('2026')
Source: "{#ManifestFile}"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Flags: ignoreversion; Check: InstallFor('2026')
#endif

#if FileExists(BinRoot + "\Release R27\CodeCompliance.dll")
Source: "{#BinRoot}\Release R27\CodeCompliance.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2027\CodeCompliance"; Flags: ignoreversion; Check: InstallFor('2027')
Source: "{#ManifestFile}"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2027"; Flags: ignoreversion; Check: InstallFor('2027')
#endif

[Code]
function RevitInstalled(Ver: String): Boolean;
begin
  // A Revit version counts as present if its program folder exists or the user
  // already has an Addins folder for it (covers non-default install locations).
  Result := DirExists(ExpandConstant('{commonpf64}\Autodesk\Revit ' + Ver)) or
            DirExists(ExpandConstant('{userappdata}\Autodesk\Revit\Addins\' + Ver));
end;

function AnyRevitDetected(): Boolean;
begin
  Result := RevitInstalled('2024') or RevitInstalled('2025') or
            RevitInstalled('2026') or RevitInstalled('2027');
end;

function InstallFor(Ver: String): Boolean;
begin
  // Install for detected versions; if nothing was detected, install for all
  // versions so the add-in is ready whenever Revit appears.
  Result := RevitInstalled(Ver) or not AnyRevitDetected();
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not AnyRevitDetected() then
    MsgBox('No installation of Revit 2024-2027 was detected on this computer.' + #13#10 +
           'The add-in will be installed for all supported versions anyway, and will be ' +
           'picked up automatically once Revit is installed.', mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    MsgBox('Installation complete.' + #13#10#13#10 +
           'Start Revit and choose "Always Load" when asked about the new add-in. ' +
           'You will find an "APG Revit Plugins" tab in the ribbon with the ' +
           'Code Compliance - Fire Fighting and Ramp Creator plugins.', mbInformation, MB_OK);
end;
