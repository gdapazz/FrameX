# Funções compartilhadas pelos scripts. Uso: . "$PSScriptRoot\common.ps1"
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Test-Dotnet10 {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        return $false
    }

    return [bool](& dotnet --list-sdks | Where-Object { $_ -match '^10\.' })
}

function Initialize-Dotnet {
    if (-not (Test-Dotnet10)) {
        # O dotnet do PATH pode ser só um runtime; tenta a instalação por usuário (dotnet-install.ps1).
        $userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
        if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) {
            $env:DOTNET_ROOT = $userDotnet
            $env:PATH = "$userDotnet;$env:PATH"
        }
        elseif (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw 'SDK do .NET não encontrado. Instale o .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0'
        }
    }

    $sdks = & dotnet --list-sdks
    if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
        throw "O FRAMEX requer o .NET 10 SDK. SDKs encontrados:`n$($sdks -join "`n")"
    }
}

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Falhou ($LASTEXITCODE): $File $($Arguments -join ' ')"
    }
}
