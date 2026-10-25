<#
Publishes and packages a new Browse release with Velopack, then uploads it as a GitHub Release.

Two tokens, both read from $TokenFile (plain text: a label line, then the token on the next line):
  - "Publish": your read/write token. Used only by this script, never embedded.
  - "Embedded Browse" (falls back to "Embedded"): read-only fine-grained token (Contents: Read, this repo
    only) baked into the exe as assembly metadata so installed copies can check for updates.

Prerequisite (once): dotnet tool install -g vpk
Usage:  .\scripts\release.ps1 -Version 1.0.1   (bump <Version> in Browse.csproj first)
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$RepoUrl = "https://github.com/sebi3003/Browse",
    [string]$GitHubToken,
    [string]$TokenFile = "C:\Users\sebastian\OneDrive\DevelopmentProjects\Windows\GIT_TOKEN.txt"
)

$ErrorActionPreference = "Stop"

function Read-LabeledToken([string]$path, [string]$label) {
    if (-not (Test-Path $path)) { return $null }
    $lines = Get-Content $path
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i].Trim() -eq $label) {
            for ($j = $i + 1; $j -lt $lines.Length; $j++) {
                if ($lines[$j].Trim()) { return $lines[$j].Trim() }
            }
        }
    }
    return $null
}

# PowerShell doesn't stop on a failing native exe, so check each one explicitly.
function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed with exit code $LASTEXITCODE. See output above." }
}

$publishToken = if ($GitHubToken) { $GitHubToken } elseif ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { Read-LabeledToken $TokenFile "Publish" }
if (-not $publishToken) { throw "No publish token. Pass -GitHubToken, set GITHUB_TOKEN, or add a 'Publish' entry to $TokenFile." }

$embeddedToken = Read-LabeledToken $TokenFile "Embedded Browse"
if (-not $embeddedToken) { $embeddedToken = Read-LabeledToken $TokenFile "Embedded" }
if (-not $embeddedToken) { throw "No 'Embedded Browse' / 'Embedded' token found in $TokenFile." }

$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "Browse.csproj"

$csprojVersion = (dotnet msbuild $csproj -getProperty:Version).Trim()
if ($csprojVersion -ne $Version) { throw "Browse.csproj <Version> is '$csprojVersion' but -Version '$Version' was requested. Bump the csproj first." }

$publishDir = Join-Path $root "publish\win-x64"
$packOutDir = Join-Path $root "publish\release-out"

Write-Host "Publishing v$Version (self-contained, win-x64)..."
dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -p:EmbeddedToken=$embeddedToken -o $publishDir
Assert-LastExitCode "dotnet publish"

Write-Host "Downloading existing releases (for delta packages)..."
vpk download github --repoUrl $RepoUrl --token $publishToken --outputDir $packOutDir
if ($LASTEXITCODE -ne 0) { Write-Warning "vpk download exited with $LASTEXITCODE - non-fatal on the first-ever release." }

Write-Host "Packing v$Version..."
vpk pack --packId Browse --packVersion $Version --packDir $publishDir --mainExe Browse.exe --outputDir $packOutDir --noPortable
Assert-LastExitCode "vpk pack"

Write-Host "Uploading and publishing GitHub release v$Version..."
vpk upload github --repoUrl $RepoUrl --token $publishToken --outputDir $packOutDir --publish
Assert-LastExitCode "vpk upload github"

Write-Host "Done. Release v$Version published to $RepoUrl"
