[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root 'SplitGM-VM-Decompiler.sln'
$setupScript = Join-Path $root 'Setup-Dependencies.ps1'
$cleanScript = Join-Path $root 'Clean-Solution.ps1'
$nugetConfig = Join-Path $root 'NuGet.Config'
$nugetRoot = Join-Path $root '.nuget'
$nugetPackages = Join-Path $nugetRoot 'packages'
$nugetHttpCache = Join-Path $nugetRoot 'http-cache'
$nugetPluginsCache = Join-Path $nugetRoot 'plugins-cache'
$guiProject = Join-Path $root 'src\SplitGM.Gui\SplitGM.Gui.csproj'
$launcherProject = Join-Path $root 'src\SGMVMDLauncher\SGMVMDLauncher.csproj'
$underanalyzerProject = Join-Path $root 'External\UndertaleModTool\Underanalyzer\Underanalyzer\Underanalyzer.csproj'
$umtLibProject = Join-Path $root 'External\UndertaleModTool\UndertaleModLib\UndertaleModLib.csproj'
$outRoot = Join-Path $root "artifacts\$Runtime"
$appOutput = Join-Path $outRoot 'SplitGM'
$logDirectory = Join-Path $root 'Logs'
$timestamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
$logPath = Join-Path $logDirectory "Build-$timestamp.log"
$platform = 'x64'
$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }

New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
@(
    'SplitGM-VM Decompiler v0.5.1.0 release build',
    "Started: $(Get-Date -Format o)",
    "Runtime: $Runtime",
    "Platform: $platform",
    "Self-contained: $selfContainedValue",
    "Solution: $solution",
    ''
) | Set-Content -LiteralPath $logPath -Encoding UTF8

function Write-BuildLine {
    param([string]$Text, [ConsoleColor]$Color = [ConsoleColor]::Gray)
    Write-Host $Text -ForegroundColor $Color
    Add-Content -LiteralPath $logPath -Value $Text -Encoding UTF8
}

function Invoke-DotNetRaw {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Write-BuildLine "" DarkGray
    Write-BuildLine "=== $Description ===" Cyan
    Write-BuildLine ("dotnet " + ($Arguments -join ' ')) DarkGray

    & dotnet @Arguments 2>&1 | ForEach-Object {
        $line = $_.ToString()
        Write-Host $line
        Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    }

    return $LASTEXITCODE
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $exitCode = Invoke-DotNetRaw $Description $Arguments
    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }
}

try {
    Write-BuildLine "Build log: $logPath" Cyan

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET SDK was not found. Install the .NET 10 SDK or the Visual Studio 2026 .NET desktop workload.'
    }

    # Do not use the user-wide NuGet package directory. A partially extracted package
    # there can produce misleading NU1202 errors saying that otherwise valid packages
    # support no target frameworks. The repository-local cache is safe to delete/retry.
    New-Item -ItemType Directory -Force -Path $nugetPackages | Out-Null
    New-Item -ItemType Directory -Force -Path $nugetHttpCache | Out-Null
    New-Item -ItemType Directory -Force -Path $nugetPluginsCache | Out-Null
    $env:NUGET_PACKAGES = $nugetPackages
    $env:NUGET_HTTP_CACHE_PATH = $nugetHttpCache
    $env:NUGET_PLUGINS_CACHE_PATH = $nugetPluginsCache

    Write-BuildLine "NuGet package cache: $nugetPackages" DarkGray
    Write-BuildLine "NuGet configuration: $nugetConfig" DarkGray

    Write-BuildLine 'Verifying the pinned UndertaleModTool and matching Underanalyzer revisions...' Yellow
    & $setupScript 2>&1 | ForEach-Object {
        $line = $_.ToString()
        Write-Host $line
        Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    }
    if (-not (Test-Path -LiteralPath $umtLibProject) -or
        -not (Test-Path -LiteralPath $underanalyzerProject)) {
        throw 'Dependency setup completed without the expected UndertaleModLib/Underanalyzer project files.'
    }

    if (-not $NoClean) {
        Write-BuildLine 'Cleaning stale Visual Studio, bin, and obj data...' Yellow
        & $cleanScript 2>&1 | ForEach-Object {
            $line = $_.ToString()
            Write-Host $line
            Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
        }
    }

    Remove-Item -LiteralPath $outRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

    $restoreArguments = @(
        'restore', $solution,
        "-p:Platform=$platform",
        '--configfile', $nugetConfig,
        '--force',
        '--no-cache'
    )

    $restoreExitCode = Invoke-DotNetRaw 'Restore solution using isolated NuGet cache' $restoreArguments
    if ($restoreExitCode -ne 0) {
        Write-BuildLine 'The first restore failed. Rebuilding the project-local NuGet cache and retrying once...' Yellow
        Remove-Item -LiteralPath $nugetRoot -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $nugetPackages | Out-Null
        New-Item -ItemType Directory -Force -Path $nugetHttpCache | Out-Null
        New-Item -ItemType Directory -Force -Path $nugetPluginsCache | Out-Null

        $restoreExitCode = Invoke-DotNetRaw 'Restore solution after NuGet cache repair' $restoreArguments
        if ($restoreExitCode -ne 0) {
            throw "Restore solution failed after the automatic NuGet cache repair with exit code $restoreExitCode."
        }
    }

    # Build the two external projects first. This guarantees their reference assemblies
    # exist before SplitGM.Core is compiled, including inside Visual Studio x64 builds.
    Invoke-DotNet 'Build Underanalyzer' @(
        'build', $underanalyzerProject,
        '-c', 'Release',
        "-p:Platform=$platform",
        '--no-restore'
    )

    Invoke-DotNet 'Build UndertaleModLib' @(
        'build', $umtLibProject,
        '-c', 'Release',
        "-p:Platform=$platform",
        '--no-restore'
    )

    Invoke-DotNet 'Build SplitGM solution' @(
        'build', $solution,
        '-c', 'Release',
        "-p:Platform=$platform",
        '--no-restore'
    )

    Invoke-DotNet 'Publish GUI' @(
        'publish', $guiProject,
        '-c', 'Release',
        '-r', $Runtime,
        '--self-contained', $selfContainedValue,
        "-p:Platform=$platform",
        '-o', $appOutput
    )

    Invoke-DotNet 'Publish required animated launcher' @(
        'publish', $launcherProject,
        '-c', 'Release',
        '-r', $Runtime,
        '--self-contained', $selfContainedValue,
        "-p:Platform=$platform",
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-o', $appOutput
    )

    $launcherExe = Join-Path $appOutput 'SGMVMDLauncher.exe'
    $mainExe = Join-Path $appOutput 'SplitGM-VM-Decompiler.exe'
    if (-not (Test-Path -LiteralPath $launcherExe)) {
        throw 'Launcher publish completed without SGMVMDLauncher.exe.'
    }
    if (-not (Test-Path -LiteralPath $mainExe)) {
        throw 'GUI publish completed without SplitGM-VM-Decompiler.exe.'
    }

    Copy-Item (Join-Path $root 'LICENSE.txt') $outRoot -Force
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $outRoot -Force
    Copy-Item (Join-Path $root 'README.md') $outRoot -Force

    Write-BuildLine "" DarkGray
    Write-BuildLine "Release output: $outRoot" Green
    Write-BuildLine "Build log: $logPath" Green
    Write-BuildLine "Finished: $(Get-Date -Format o)" Green
    exit 0
}
catch {
    $details = ($_ | Out-String).TrimEnd()
    Write-Host $details -ForegroundColor Red
    Add-Content -LiteralPath $logPath -Value @('', 'BUILD FAILED', $details, "Finished: $(Get-Date -Format o)") -Encoding UTF8
    Write-Host "Build failed. Full log: $logPath" -ForegroundColor Red
    exit 1
}
