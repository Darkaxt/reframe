<#
.SYNOPSIS
  Puts the patched terminal control package (Reframe.Terminal.Wpf, built by .github/workflows/terminal-wpf.yml) in
  wpf/packages-local, the local feed nuget.config names, after checking it against the SHA-256 pinned here.
  CI's wpf job runs it before dotnet build; run it once before a local build too (needs gh, or a public download).
#>
param([string]$Out = (Join-Path $PSScriptRoot 'packages-local'))
$ErrorActionPreference = 'Stop'

# Keep in step with Reframe.Shell.csproj's PackageReference and the workflow's PACKAGE_VERSION / RELEASE_TAG.
$Version = '1.25.3131-reframe.2'
$Tag = 'terminal-wpf-v1.25.3131.0-reframe.2'
$Sha256 = 'E9951B082D5C279077119C1A55D6AC4A1E315FDD18A0BE22FFD4DDEDF547C86C'

$name = "Reframe.Terminal.Wpf.$Version.nupkg"
$file = Join-Path $Out $name
New-Item -ItemType Directory -Force $Out | Out-Null
if (-not (Test-Path $file)) {
    $url = "https://github.com/Darkaxt/reframe/releases/download/$Tag/$name"
    Invoke-WebRequest -Uri $url -OutFile "$file.part" -UseBasicParsing
    Move-Item -Force "$file.part" $file
}
$stream = [IO.File]::OpenRead($file)
try { $actual = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)) -replace "-", "" } finally { $stream.Dispose() }
if ($actual -ne $Sha256) {
    Remove-Item -Force $file
    throw "$name has SHA-256 $actual, expected $Sha256"
}
Write-Host "$name OK ($actual)"
