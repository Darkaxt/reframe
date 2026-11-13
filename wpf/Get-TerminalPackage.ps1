<#
.SYNOPSIS
  Puts the patched terminal control package (Reframe.Terminal.Wpf, built by .github/workflows/terminal-wpf.yml) in
  wpf/packages-local, the local feed nuget.config names, after checking it against the SHA-256 pinned here.
  CI's wpf job runs it before dotnet build; run it once before a local build too (needs gh, or a public download).
#>
param([string]$Out = (Join-Path $PSScriptRoot 'packages-local'))
$ErrorActionPreference = 'Stop'

# Keep in step with Reframe.Shell.csproj's PackageReference and the workflow's PACKAGE_VERSION / RELEASE_TAG.
$Version = '1.25.3131-reframe.1'
$Tag = 'terminal-wpf-v1.25.3131.0-reframe.1'
$Sha256 = 'PENDING'

$name = "Reframe.Terminal.Wpf.$Version.nupkg"
$file = Join-Path $Out $name
New-Item -ItemType Directory -Force $Out | Out-Null
if (-not (Test-Path $file)) {
    $url = "https://github.com/Darkaxt/reframe/releases/download/$Tag/$name"
    Invoke-WebRequest -Uri $url -OutFile "$file.part" -UseBasicParsing
    Move-Item -Force "$file.part" $file
}
$actual = (Get-FileHash -Algorithm SHA256 $file).Hash
if ($actual -ne $Sha256) {
    Remove-Item -Force $file
    throw "$name has SHA-256 $actual, expected $Sha256"
}
Write-Host "$name OK ($actual)"
