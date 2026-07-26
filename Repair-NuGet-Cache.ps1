[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$localNuGetRoot = Join-Path $root '.nuget'

Write-Host 'SplitGM local NuGet cache repair' -ForegroundColor Cyan

if (Test-Path -LiteralPath $localNuGetRoot) {
    Write-Host "Removing $localNuGetRoot" -ForegroundColor Yellow
    Remove-Item -LiteralPath $localNuGetRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path (Join-Path $localNuGetRoot 'packages') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $localNuGetRoot 'http-cache') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $localNuGetRoot 'plugins-cache') | Out-Null

Write-Host 'The project-local NuGet cache is clean. Run Build-Release.ps1 again.' -ForegroundColor Green
