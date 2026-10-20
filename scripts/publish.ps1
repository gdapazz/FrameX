<#
.SYNOPSIS
Publica o FRAMEX (win-x64, autônomo) em artifacts/publish/win-x64 e, se o Inno Setup 6 estiver
instalado, gera o instalador em artifacts/installer.
.PARAMETER SkipTests
Não executa os testes antes de publicar.
.PARAMETER SkipInstaller
Não gera o instalador mesmo que o Inno Setup esteja disponível.
#>
param(
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

. "$PSScriptRoot\common.ps1"
Initialize-Dotnet

if (-not $SkipTests) {
    & "$PSScriptRoot\test.ps1" -Configuration Release
}

$project = Join-Path $RepoRoot 'src\Framex.App\Framex.App.csproj'
Invoke-Checked dotnet @('publish', $project, '-p:PublishProfile=win-x64', '-nologo')

$publishDir = Join-Path $RepoRoot 'artifacts\publish\win-x64'
$exe = Join-Path $publishDir 'Framex.exe'
if (-not (Test-Path $exe)) {
    throw "Publicação não gerou $exe"
}

# O serviço vai em uma subpasta própria (runtime autônomo separado), depois do app, porque a publicação do app limpa a pasta.
$serviceProject = Join-Path $RepoRoot 'src\Framex.Service\Framex.Service.csproj'
$serviceDir = Join-Path $publishDir 'service'
Invoke-Checked dotnet @('publish', $serviceProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=embedded', '-o', $serviceDir, '-nologo')
if (-not (Test-Path (Join-Path $serviceDir 'Framex.Service.exe'))) {
    throw "Publicação não gerou o serviço em $serviceDir"
}

$redistEarly = Join-Path $RepoRoot 'installer\redist'
$toolsOut = Join-Path $publishDir 'tools'
New-Item -ItemType Directory -Force $toolsOut | Out-Null
Get-ChildItem $redistEarly -Filter 'PresentMon-*.exe' -ErrorAction SilentlyContinue | Copy-Item -Destination $toolsOut -Force

$version = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "Publicado: $publishDir (versão $version, com o serviço em service\)" -ForegroundColor Green

if ($SkipInstaller) {
    return
}

$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning 'Inno Setup 6 não encontrado; instalador não gerado. Instale em https://jrsoftware.org/isinfo.php e execute novamente.'
    return
}

$redist = Join-Path $RepoRoot 'installer\redist'
$missing = @('PawnIO_setup.exe', 'licenses\PawnIO-LICENSE.txt', 'licenses\PresentMon-LICENSE.txt') | Where-Object { -not (Test-Path (Join-Path $redist $_)) }
if (-not (Get-ChildItem $redist -Filter 'PresentMon-*.exe' -ErrorAction SilentlyContinue)) { $missing += 'PresentMon-*.exe' }
if ($missing) {
    throw "Faltam arquivos em installer\redist: $($missing -join ', '). Veja docs/COMPILACAO.md."
}

foreach ($signed in @(Get-ChildItem $redist -Filter '*.exe')) {
    $sig = Get-AuthenticodeSignature $signed.FullName
    if ($sig.Status -ne 'Valid') {
        throw "Assinatura digital inválida em $($signed.Name): $($sig.Status)"
    }
}

$semver = ($version -split '\+')[0]
Invoke-Checked $iscc @("/DAppVersion=$semver", "/DPublishDir=$publishDir", (Join-Path $RepoRoot 'installer\framex.iss'))
Write-Host "Instalador gerado: $(Join-Path $RepoRoot 'Setup.exe')" -ForegroundColor Green
