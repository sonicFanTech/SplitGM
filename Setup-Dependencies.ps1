[CmdletBinding()]
param(
    [switch]$UseLatest,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# GitHub requires TLS 1.2 on older Windows PowerShell builds.
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}
catch {
    # Modern PowerShell/.NET selects a secure protocol automatically.
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$external = Join-Path $root 'External'
$umtPath = Join-Path $external 'UndertaleModTool'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('SplitGM-Dependencies-' + [Guid]::NewGuid().ToString('N'))
$revisionMarkerPath = Join-Path $umtPath '.splitgm-dependency-revisions.json'
$pinnedCommit = '2b6fe69722cec25219f1ae21f8111907c2a15629'
$headers = @{
    'User-Agent' = 'SplitGM-VM-Decompiler-v0.5.1.0-Dependency-Setup'
    'Accept' = 'application/vnd.github+json'
}

function Write-Step([string]$Message) {
    Write-Host $Message -ForegroundColor Green
}

function Download-ZipRepository {
    param(
        [Parameter(Mandatory = $true)][string]$Owner,
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Revision,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $zipPath = Join-Path $tempRoot ($Repository + '.zip')
    $extractPath = Join-Path $tempRoot ($Repository + '-extract')
    $downloadUrl = "https://codeload.github.com/$Owner/$Repository/zip/$Revision"

    Write-Step "Downloading $Owner/$Repository at $Revision..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing

    New-Item -ItemType Directory -Force -Path $extractPath | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractPath -Force

    $sourceDirectory = Get-ChildItem -LiteralPath $extractPath -Directory | Select-Object -First 1
    if ($null -eq $sourceDirectory) {
        throw "The downloaded $Repository archive did not contain a source directory."
    }

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    Get-ChildItem -LiteralPath $sourceDirectory.FullName -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

Write-Host 'SplitGM-VM Decompiler dependency setup' -ForegroundColor Cyan
Write-Host 'Git and winget are not required by this setup script.' -ForegroundColor DarkGray

New-Item -ItemType Directory -Force -Path $external | Out-Null
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

try {
    if ($UseLatest) {
        Write-Step 'Resolving the latest UndertaleModTool master revision...'
        $commitInfo = Invoke-RestMethod `
            -Uri 'https://api.github.com/repos/UnderminersTeam/UndertaleModTool/commits/master' `
            -Headers $headers
        $umtRevision = [string]$commitInfo.sha
    }
    else {
        $umtRevision = $pinnedCommit
        Write-Host "Using tested UndertaleModTool revision $umtRevision" -ForegroundColor Green
    }

    if ((Test-Path -LiteralPath $umtPath) -and -not $Force) {
        $libProject = Join-Path $umtPath 'UndertaleModLib\UndertaleModLib.csproj'
        $underanalyzerProject = Join-Path $umtPath 'Underanalyzer\Underanalyzer\Underanalyzer.csproj'
        $installedUmtRevision = ''

        if (Test-Path -LiteralPath $revisionMarkerPath) {
            try {
                $revisionMarker = Get-Content -LiteralPath $revisionMarkerPath -Raw | ConvertFrom-Json
                $installedUmtRevision = [string]$revisionMarker.undertaleModTool
            }
            catch {
                Write-Host 'The dependency revision marker is unreadable; dependencies will be refreshed.' -ForegroundColor Yellow
            }
        }

        if ((Test-Path -LiteralPath $libProject) -and
            (Test-Path -LiteralPath $underanalyzerProject) -and
            ($installedUmtRevision -eq $umtRevision)) {
            Write-Host "The requested UndertaleModTool revision is already installed: $umtRevision" -ForegroundColor Yellow
            Write-Host ''
            Write-Host 'Dependencies are ready.' -ForegroundColor Cyan
            Write-Host 'Open SplitGM-VM-Decompiler.sln in Visual Studio 2026 and build SplitGM.Gui.'
            return
        }

        Write-Host 'Existing dependencies are missing, unversioned, or from another revision; refreshing them...' -ForegroundColor Yellow
        Remove-Item -LiteralPath $umtPath -Recurse -Force
    }
    elseif ($Force -and (Test-Path -LiteralPath $umtPath)) {
        Write-Host 'Removing the existing dependency folder...' -ForegroundColor Yellow
        Remove-Item -LiteralPath $umtPath -Recurse -Force
    }

    Write-Step 'Resolving the matching Underanalyzer submodule revision...'
    $submoduleInfo = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/UnderminersTeam/UndertaleModTool/contents/Underanalyzer?ref=$umtRevision" `
        -Headers $headers

    $underanalyzerRevision = [string]$submoduleInfo.sha
    if ([string]::IsNullOrWhiteSpace($underanalyzerRevision)) {
        throw 'GitHub did not return the Underanalyzer submodule revision.'
    }

    Download-ZipRepository `
        -Owner 'UnderminersTeam' `
        -Repository 'UndertaleModTool' `
        -Revision $umtRevision `
        -Destination $umtPath

    $underanalyzerPath = Join-Path $umtPath 'Underanalyzer'
    Download-ZipRepository `
        -Owner 'UnderminersTeam' `
        -Repository 'Underanalyzer' `
        -Revision $underanalyzerRevision `
        -Destination $underanalyzerPath

    $libProject = Join-Path $umtPath 'UndertaleModLib\UndertaleModLib.csproj'
    $underanalyzerProject = Join-Path $umtPath 'Underanalyzer\Underanalyzer\Underanalyzer.csproj'

    if (-not (Test-Path -LiteralPath $libProject)) {
        throw "Missing expected project after download: $libProject"
    }
    if (-not (Test-Path -LiteralPath $underanalyzerProject)) {
        throw "Missing expected project after download: $underanalyzerProject"
    }

    [ordered]@{
        undertaleModTool = $umtRevision
        underanalyzer = $underanalyzerRevision
        installedBy = 'SplitGM-VM Decompiler v0.5.1.0'
        installedUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $revisionMarkerPath -Encoding UTF8

    Write-Host ''
    Write-Host 'Dependencies are ready.' -ForegroundColor Cyan
    Write-Host "UndertaleModTool: $umtRevision" -ForegroundColor DarkGray
    Write-Host "Underanalyzer:     $underanalyzerRevision" -ForegroundColor DarkGray
    Write-Host 'Open SplitGM-VM-Decompiler.sln in Visual Studio 2026 and build SplitGM.Gui.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
