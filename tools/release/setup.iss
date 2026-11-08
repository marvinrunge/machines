; The Windows installer around the release folder (Inno Setup 6), which tools/release/build-windows.sh compiles:
;   ISCC /DVersion=<version> /DSource=<release folder> /O<output folder> tools/release/setup.iss
; It installs for the player alone, without administrator rights, into their own programs folder: the game keeps
; its import (classic), saves and replays next to Machines.exe, which Program Files would not let it write. The first
; start then finds the player's copy of the game (GameLocator) and imports it with one button. Nothing from the
; game itself is in the installer.

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Source
  #error Source must name the release folder: /DSource=build\Machines-Remake-<version>
#endif

[Setup]
AppId={{6E3C1F2A-8B4D-4E7A-9C51-2D7F0A6B3E19}
AppName=Machines Remake
AppVersion={#Version}
AppVerName=Machines Remake {#Version}
AppPublisherURL=https://github.com/marvinrunge/machines
AppSupportURL=https://github.com/marvinrunge/machines/issues
DefaultDirName={autopf}\Machines Remake
DefaultGroupName=Machines Remake
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=Machines-Remake-{#Version}-setup
UninstallDisplayIcon={app}\Machines.exe
UninstallDisplayName=Machines Remake
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.RemoveImport=Also remove the game data imported from your copy of Machines (the classic folder, a few GB)? Your saves and replays stay either way.
de.RemoveImport=Auch die aus deiner Machines-Kopie importierten Spieldaten entfernen (den Ordner classic, ein paar GB)? Deine Spielstände und Wiederholungen bleiben so oder so.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; What each release brings whole, so that an update leaves nothing of the last one behind. The import, saves and
; replays are the player's and stay.
Type: filesandordirs; Name: "{app}\data"
Type: filesandordirs; Name: "{app}\data_Machines_windows_x86_64"
Type: filesandordirs; Name: "{app}\tools"
Type: filesandordirs; Name: "{app}\textures"
Type: filesandordirs; Name: "{app}\models"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Machines Remake"; Filename: "{app}\Machines.exe"
Name: "{group}\{cm:UninstallProgram,Machines Remake}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Machines Remake"; Filename: "{app}\Machines.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Machines.exe"; Description: "{cm:LaunchProgram,Machines Remake}"; Flags: nowait postinstall skipifsilent

[Code]
// The import is made on the player's computer from their copy, so the uninstaller leaves it unless they say.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Classic: String;
begin
  Classic := ExpandConstant('{app}\classic');
  if (CurUninstallStep = usPostUninstall) and DirExists(Classic) and not UninstallSilent then
    if MsgBox(CustomMessage('RemoveImport'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(Classic, True, True, True);
end;
