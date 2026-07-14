[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host 'Applying SplitGM v0.5.0 upgrade cleanup...' -ForegroundColor Cyan

$obsoleteCli = Join-Path $root 'src\SplitGM.Cli'
if (Test-Path -LiteralPath $obsoleteCli) {
    Remove-Item -LiteralPath $obsoleteCli -Recurse -Force
    Write-Host 'Removed the retired SplitGM.Cli project.' -ForegroundColor Yellow
}

$obsoleteUpgrade = Join-Path $root 'Apply-v0.4.0-Upgrade.ps1'
if (Test-Path -LiteralPath $obsoleteUpgrade) {
    Remove-Item -LiteralPath $obsoleteUpgrade -Force
}

$clean = Join-Path $root 'Clean-Solution.ps1'
if (Test-Path -LiteralPath $clean) {
    & $clean
}

Write-Host ''
Write-Host 'SplitGM v0.5.0 files are ready.' -ForegroundColor Green
Write-Host 'Run Setup-Dependencies.ps1, open SplitGM-VM-Decompiler.sln, select Release | x64, and rebuild the solution.' -ForegroundColor Green
