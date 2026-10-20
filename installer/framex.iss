; Instalador do FRAMEX (Inno Setup 6). Gere com scripts/publish.ps1, que passa AppVersion e PublishDir.
; Instalação para todos os usuários em Program Files (o instalador pede administrador; o app em si roda sem elevação).
; Instala também:
;   - o serviço "FramexService" (LocalSystem), que lê os sensores da CPU e executa o PresentMon para o app;
;   - o driver PawnIO oficial (assinado, GPL-2.0), exigido pelo LibreHardwareMonitor para temperatura/clock/consumo da CPU;
;   - o PresentMon (Intel, MIT) em {app}\tools, pasta que só administradores podem alterar.
; Os dados do usuário (%LOCALAPPDATA%\FRAMEX) são preservados na desinstalação. Saída: Setup.exe na raiz do repositório.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#define ServiceName "FramexService"

[Setup]
AppId={{6F1D3A52-8C4B-4E8B-9B7A-3E2C1F0A9D41}
AppName=FRAMEX
AppVersion={#AppVersion}
AppVerName=FRAMEX {#AppVersion}
AppPublisher=Gdapazz
DefaultDirName={autopf}\FRAMEX
DefaultGroupName=FRAMEX
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..
OutputBaseFilename=Setup
SetupIconFile=..\src\Framex.App\Assets\framex.ico
UninstallDisplayIcon={app}\Framex.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\FRAMEX.SingleInstance
CloseApplications=yes
RestartApplications=no
InfoBeforeFile=..\THIRD-PARTY-NOTICES.md

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "redist\PresentMon-*.exe"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "redist\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "redist\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\FRAMEX"; Filename: "{app}\Framex.exe"
Name: "{autodesktop}\FRAMEX"; Filename: "{app}\Framex.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "-install -silent"; StatusMsg: "Instalando o driver PawnIO (sensores da CPU)..."; Flags: waituntilterminated; Check: not PawnIOInstalled
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""{app}\service\Framex.Service.exe"" start= auto obj= LocalSystem DisplayName= ""FRAMEX"""; StatusMsg: "Registrando o serviço do FRAMEX..."; Flags: runhidden waituntilterminated; Check: not ServiceExists
Filename: "{sys}\sc.exe"; Parameters: "config {#ServiceName} binPath= ""{app}\service\Framex.Service.exe"" start= auto obj= LocalSystem DisplayName= ""FRAMEX"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Sensores de CPU e captura de FPS para o FRAMEX, sem exigir que o app rode como administrador."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; StatusMsg: "Iniciando o serviço do FRAMEX..."; Flags: runhidden waituntilterminated
Filename: "{app}\Framex.exe"; Description: "Abrir o FRAMEX"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopFramexService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteFramexService"

[Code]
function PawnIOInstalled: Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO', 'DisplayVersion', Version) and (Version <> '');
end;

function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\{#ServiceName}');
end;

{ Em uma atualização, o serviço precisa parar antes de os arquivos serem substituídos. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if ServiceExists then
    Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { Remove a inicialização com o Windows, se o usuário a tiver ativado no app. }
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'FRAMEX');
  end;
end;
