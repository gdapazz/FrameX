<#
.SYNOPSIS
Executa os testes unitários/integração e, opcionalmente, a verificação offscreen das telas.
.PARAMETER UiSmoke
Também renderiza todas as telas em PNG (artifacts/ui-smoke) e falha se houver erros de binding.
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$UiSmoke
)

. "$PSScriptRoot\common.ps1"
Initialize-Dotnet

$results = Join-Path $RepoRoot 'artifacts\test-results'
Invoke-Checked dotnet @('test', (Join-Path $RepoRoot 'Framex.slnx'), '-c', $Configuration, '-nologo',
    '--logger', 'trx;LogFileName=framex-tests.trx', '--results-directory', $results)
Write-Host "Testes aprovados. Resultados: $results" -ForegroundColor Green

if ($UiSmoke) {
    $out = Join-Path $RepoRoot 'artifacts\ui-smoke'
    Invoke-Checked dotnet @('run', '--project', (Join-Path $RepoRoot 'tools\Framex.UiSmoke'), '-c', $Configuration, '--no-build', '--', $out)
    Write-Host "Telas renderizadas em $out" -ForegroundColor Green
}
