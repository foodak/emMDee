<#
.SYNOPSIS
    Fully publish a new release of emMDee.
.DESCRIPTION
    Prompts for tag version and release description, then:
    - Clears publish/ and builds win-x64 + win-arm64 self-contained single-file to publish/
    - Zips each into emMDee-{rid}.zip
    - Creates GitHub release with description and uploads both zips (tag is auto-created by gh)
.PARAMETER DryRun
    Print what would be done without making changes.
.PARAMETER SkipBuild
    Skip dotnet publish (useful if you already built).
.PARAMETER TagPrefix
    Prefix for the tag (default 'v').
.PARAMETER Tag
    Tag version number (e.g. '1.0.3'). If omitted, prompts interactively.
.PARAMETER Notes
    Release description/notes. If omitted, prompts interactively (defaults to last commit message).
.EXAMPLE
    .\PublishRelease.ps1
.EXAMPLE
    .\PublishRelease.ps1 -Tag '1.0.3' -Notes 'Fixed critical bug'
.EXAMPLE
    .\PublishRelease.ps1 -DryRun
#>

[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$SkipBuild,
    [string]$TagPrefix = 'v',
    [string]$Tag,
    [string]$Notes
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

# ── helpers ──────────────────────────────────────────────────────────
function header($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# ── prerequisites ────────────────────────────────────────────────────
header 'Prerequisites'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet SDK not found. Install .NET 8+ SDK.'
    exit 1
}
Write-Host "dotnet  : $(dotnet --version)"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error 'GitHub CLI (gh) not found. Install from https://cli.github.com'
    exit 1
}
Write-Host "gh      : $(gh --version | Select-Object -First 1)"

Push-Location $repoRoot
if (-not (Test-Path '.git')) {
    Write-Error 'Not in a git repository. Run from repo root.'
    exit 1
}

# ── get tag ──────────────────────────────────────────────────────────
header 'Version'

# gh release create makes the tag on the remote, so the local tag list goes
# stale after every publish. Sync tags from origin first (prune deleted ones)
# so "latest existing tag" reflects what's actually been released.
git fetch --tags --prune --prune-tags origin 2>&1 | Out-Null

$allTags = git tag --sort=-v:refname | Where-Object { $_ -match '^v?\d+\.\d+\.\d+$' }
$latestTag = if ($allTags) { $allTags | Select-Object -First 1 } else { 'v0.0.0' }
Write-Host "Latest existing tag: $latestTag"

# auto-suggest next patch bump
$verMatch = [regex]::Match($latestTag, '^v?(\d+)\.(\d+)\.(\d+)$')
$suggested = ''
if ($verMatch.Success) {
    $maj = [int]$verMatch.Groups[1].Value
    $min = [int]$verMatch.Groups[2].Value
    $pat = [int]$verMatch.Groups[3].Value + 1
    $suggested = "$maj.$min.$pat"
}

if (-not $Tag) {
    $prompt = if ($suggested) { "Enter tag version [$suggested]" } else { "Enter tag version" }
    $Tag = Read-Host $prompt
    if (-not $Tag) { $Tag = $suggested }
}

if (-not ($Tag -match '^\d+\.\d+\.\d+$')) {
    Write-Error "Invalid version format. Expected X.Y.Z (e.g. 1.0.3)"
    exit 1
}

$newTag = "$TagPrefix$Tag"
Write-Host "Tag    : $newTag"

# ── get release notes ────────────────────────────────────────────────
header 'Release notes'

$lastCommitMsg = git log -1 --format='%s'
$lastCommitBody = git log -1 --format='%b'
$defaultNotes = if ($lastCommitBody) { "$lastCommitMsg`n`n$lastCommitBody" } else { $lastCommitMsg }

if (-not $Notes) {
    Write-Host "Last commit: $lastCommitMsg" -ForegroundColor DarkGray
    Write-Host "Enter release description (press Enter to use last commit message):" -ForegroundColor Yellow
    $Notes = Read-Host
    if (-not $Notes) { $Notes = $defaultNotes }
}

Write-Host "Description:" -ForegroundColor Yellow
Write-Host "  $Notes" -ForegroundColor Gray

# ── confirm ──────────────────────────────────────────────────────────
Write-Host ''
Write-Host "Will create tag $newTag and publish release." -ForegroundColor Yellow
if (-not $DryRun) {
    $confirm = Read-Host "Proceed? (y/N)"
    if ($confirm -ne 'y' -and $confirm -ne 'Y') {
        Write-Host 'Aborted.'
        exit 0
    }
}

# ── build ────────────────────────────────────────────────────────────
$rids = @('win-x64', 'win-arm64')
$zipFiles = @()

foreach ($rid in $rids) {
    header "Build $rid"

    $publishDir = Join-Path $repoRoot "publish\$rid"

    if (-not $SkipBuild) {
        # Clean publish directory first
        if (Test-Path $publishDir) {
            if ($DryRun) {
                Write-Host "[DRY RUN] Remove-Item -Recurse -Force $publishDir" -ForegroundColor DarkGray
            } else {
                Remove-Item -Recurse -Force $publishDir
            }
        }

        $publishArgs = @(
            'publish', 'src/emMDee.csproj',
            '-c', 'Release',
            '-r', $rid,
            '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            '-o', $publishDir
        )
        if ($DryRun) {
            Write-Host "[DRY RUN] dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
        } else {
            dotnet @publishArgs
            if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed for $rid"; exit 1 }
        }
    } else {
        Write-Host "Skipping build for $rid (-SkipBuild)"
        if (-not (Test-Path $publishDir)) {
            Write-Error "Publish dir not found: $publishDir (run without -SkipBuild first)"
            exit 1
        }
    }

    # Create zip in publish/
    $zipFile = Join-Path $repoRoot "publish\emMDee-$rid.zip"
    if (Test-Path $zipFile) { Remove-Item $zipFile -Force }
    if ($DryRun) {
        Write-Host "[DRY RUN] Compress-Archive -Path $publishDir\* -DestinationPath $zipFile" -ForegroundColor DarkGray
    } else {
        Compress-Archive -Path "$publishDir\*" -DestinationPath $zipFile
        Write-Host "Created: $zipFile"
    }
    $zipFiles += $zipFile
}

# ── create GitHub release ────────────────────────────────────────────
# gh release create handles both tag creation and release asset upload.
# Pushing the tag separately beforehand would create a lightweight tag
# without a release, causing gh to skip asset uploads (the bug we had).
header 'Create GitHub release'

$zipArgs = @()
foreach ($z in $zipFiles) {
    $zipArgs += $z
}

if (-not $DryRun) {
    gh release create $newTag $zipArgs `
        --title "$newTag" `
        --notes "$Notes" `
        --repo foodak/emMDee 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) { Write-Error "gh release create failed"; exit 1 }
    Write-Host "Release created: $newTag"
} else {
    Write-Host "[DRY RUN] gh release create $newTag $($zipArgs -join ', ') --title '$newTag' --notes '...'" -ForegroundColor DarkGray
}

# ── cleanup ──────────────────────────────────────────────────────────
Pop-Location
Write-Host "`nDone." -ForegroundColor Green
