#requires -Version 5.1
# Publishes mula to %USERPROFILE%\.mula\bin and adds that folder to the user PATH.
# Not AppData: an install made from inside an MSIX app lands in that app's private copy
# of AppData, invisible to SSH sessions.
[CmdletBinding()]
param([string]$Prefix = (Join-Path $env:USERPROFILE '.mula\bin'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Get-Process mula -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Prefix*" } | ForEach-Object {
    throw "mula está abierto ($($_.Id)). Ciérralo antes de instalar."
}
dotnet publish (Join-Path $root 'src\Mula\Mula.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $Prefix --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló ($LASTEXITCODE)." }

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($userPath -split ';' | Where-Object { $_ })
if ($parts -notcontains $Prefix) {
    [Environment]::SetEnvironmentVariable('Path', (($parts + $Prefix) -join ';'), 'User')
    Write-Host "Añadido al PATH de usuario: $Prefix (las sesiones nuevas ya lo ven)."
}
Write-Host "mula instalado en $Prefix\mula.exe"
