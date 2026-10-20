# Compilação, publicação e instalador

## Requisitos

- Windows 10 (1809+) ou 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Os scripts também aceitam uma instalação por usuário em `%LOCALAPPDATA%\Microsoft\dotnet`.
- Opcional: [Inno Setup 6](https://jrsoftware.org/isinfo.php), para gerar o instalador.

## Compilar

```powershell
./scripts/build.ps1                    # Debug
./scripts/build.ps1 -Configuration Release
```

O script usa `-warnaserror`. Há uma única supressão pontual, `WFO0003` em `Framex.App.csproj`, justificada no próprio arquivo: o DPI é definido pelo `app.manifest`.

Diretamente:

```powershell
dotnet build Framex.slnx -c Release
dotnet run --project src/Framex.App
```

## Publicar

```powershell
./scripts/publish.ps1                  # testes + publicação + instalador (se houver Inno Setup)
./scripts/publish.ps1 -SkipTests -SkipInstaller
```

O perfil `src/Framex.App/Properties/PublishProfiles/win-x64.pubxml` gera uma publicação **autônoma** (inclui o runtime do .NET; o usuário não precisa instalá-lo) em `artifacts/publish/win-x64`.

- Sem *trimming*: o WPF e a reflexão usada pelo LibreHardwareMonitor não são compatíveis com ele.
- Sem arquivo único: as bibliotecas nativas (SQLite) ficam visíveis ao lado do executável.

Equivalente manual:

```powershell
dotnet publish src/Framex.App/Framex.App.csproj -p:PublishProfile=win-x64
```

## Instalador

`installer/framex.iss` (Inno Setup 6). O `publish.ps1` chama `ISCC.exe` com `/DAppVersion` e `/DPublishDir`. O arquivo gerado é `Setup.exe` na raiz do repositório.

- instalação para **todos os usuários** em `Program Files\FRAMEX` (pede administrador);
- publica o app e, em `{app}\service`, o `Framex.Service.exe` (autônomo);
- copia o PresentMon de `installer/redist` para `{app}\tools`;
- executa `PawnIO_setup.exe -install -silent` se o PawnIO ainda não estiver instalado;
- registra o serviço Windows `FramexService` (LocalSystem, início automático) e o inicia;
- atalho no menu Iniciar e, opcionalmente, na área de trabalho;
- fecha o FRAMEX em execução antes de atualizar (mutex `Local\FRAMEX.SingleInstance`);
- na desinstalação, para e remove o serviço, remove a inicialização com o Windows (`HKCU\...\Run\FRAMEX`) e **preserva** os dados do usuário em `%LOCALAPPDATA%\FRAMEX`;
- o PawnIO **não** é removido na desinstalação (pode ser usado por outros programas);
- exibe os avisos de terceiros antes da instalação.

Os binários redistribuídos precisam estar em `installer/redist` (PresentMon, PawnIO e as licenças). O script recusa arquivos com assinatura digital inválida.

### Assinatura

O instalador e o executável não são assinados. Para distribuição pública, assine com um certificado de assinatura de código (`signtool sign /fd SHA256 /tr <timestamp> /td SHA256 ...`) o `Framex.exe` publicado e o instalador. Sem assinatura, o SmartScreen exibirá um aviso.

## Versão

A versão fica em `Directory.Build.props` (`<Version>`). A tela Sobre e o instalador a leem do executável publicado.

## PresentMon e PawnIO

O `publish.ps1` inclui o PresentMon 2.6.0 e o instalador oficial do PawnIO 2.2.0 a partir de `installer/redist`. As licenças vão para `{app}\licenses`. Não altere esses executáveis: o script exige assinatura digital válida (Intel e namazso, respectivamente).
