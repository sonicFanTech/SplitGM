[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$knownPaths = @(
    (Join-Path $root '.vs'),
    (Join-Path $root 'artifacts'),
    (Join-Path $root 'src\SplitGM.Core\bin'),
    (Join-Path $root 'src\SplitGM.Core\obj'),
    (Join-Path $root 'src\SplitGM.Gui\bin'),
    (Join-Path $root 'src\SplitGM.Gui\obj'),
    (Join-Path $root 'External\UndertaleModTool\UndertaleModLib\bin'),
    (Join-Path $root 'External\UndertaleModTool\UndertaleModLib\obj'),
    (Join-Path $root 'External\UndertaleModTool\Underanalyzer\Underanalyzer\bin'),
    (Join-Path $root 'External\UndertaleModTool\Underanalyzer\Underanalyzer\obj')
)

foreach ($path in $knownPaths) {
    if (Test-Path -LiteralPath $path) {
        Write-Host "Removing $path" -ForegroundColor DarkGray
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

Write-Host 'SplitGM build outputs and Visual Studio cache were cleaned.' -ForegroundColor Green
