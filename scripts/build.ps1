<#
.SYNOPSIS
Compila a solução inteira. Avisos do compilador são tratados como erros.
.EXAMPLE
./scripts/build.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

. "$PSScriptRoot\common.ps1"
Initialize-Dotnet

Invoke-Checked dotnet @('build', (Join-Path $RepoRoot 'Framex.slnx'), '-c', $Configuration, '-nologo', '-warnaserror')
Write-Host "Compilação concluída ($Configuration)." -ForegroundColor Green
