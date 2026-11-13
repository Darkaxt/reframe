#Requires -Version 7.4
<#
The Windows release package since the cutover (S14): the native WPF shell and the Rust PTY host, no Electron.
Takes CI's artifacts as they are (never builds): -ShellDir is the `reframe-shell` artifact (the self-contained
`dotnet publish` of wpf/Reframe.Shell, with the terminal control), -HostDir the `reframe-host` artifact
(reframe-host.exe, conpty.dll, OpenConsole.exe). Writes <PackageDirectory>\reframe-<version>-win-portable\ and its .zip:

  reframe.exe                      the shell (its apphost, renamed: the managed scripts look for reframe.exe)
  Reframe.Shell.dll, ...           the rest of the self-contained publish (.NET runtime, WPF, terminal control)
  reframe-host\                    reframe-host.exe, conpty.dll, OpenConsole.exe (HostLauncher's default source)
  history_backend.py               the history jobs' backend (beside the shell, where it looks first)
  resources\maintenance\windows\   stop-managed.ps1 and release-files.ps1 (the deploy runs the candidate's stop script)
  LICENSE

Then checks the payload: the file version is the release version, every required file is there, and the scripts,
backend and host files are byte-identical to their sources.
#>
param(
    [Parameter(Mandatory)][string]$ShellDir,
    [Parameter(Mandatory)][string]$HostDir,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$Version
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = (Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json).version }
$ShellDir = [IO.Path]::GetFullPath($ShellDir); $HostDir = [IO.Path]::GetFullPath($HostDir)
$hostFiles = @('reframe-host.exe', 'conpty.dll', 'OpenConsole.exe')
$terminal = @('Microsoft.Terminal.Wpf.dll', 'Microsoft.Terminal.Control.dll')
foreach ($f in @('Reframe.Shell.exe', 'Reframe.Shell.dll', 'Reframe.Shell.runtimeconfig.json') + $terminal) {
    if (-not (Test-Path -LiteralPath (Join-Path $ShellDir $f) -PathType Leaf)) { throw "$ShellDir is not the reframe-shell artifact: $f missing" }
}
foreach ($f in $hostFiles) { if (-not (Test-Path -LiteralPath (Join-Path $HostDir $f) -PathType Leaf)) { throw "$HostDir is not the reframe-host artifact: $f missing" } }
# Self-contained: the runtime itself is in the publish (a framework-dependent one would need .NET on the machine).
if (-not (Test-Path -LiteralPath (Join-Path $ShellDir 'hostfxr.dll'))) { throw 'The shell artifact is not self-contained (no hostfxr.dll)' }

$name = "reframe-$Version-win-portable"
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$folder = Join-Path $PackageDirectory $name
$zip = Join-Path $PackageDirectory "$name.zip"
foreach ($p in @($folder, $zip)) { if (Test-Path -LiteralPath $p) { throw "$p exists; package into a new folder" } }
[IO.Directory]::CreateDirectory($folder) | Out-Null

foreach ($file in Get-ChildItem -LiteralPath $ShellDir -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($ShellDir, $file.FullName)
    if ($relative -like '*.pdb') { continue }
    if ($relative -ceq 'Reframe.Shell.exe') { $relative = 'reframe.exe' }
    $target = Join-Path $folder $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    [IO.File]::Copy($file.FullName, $target)
}
[IO.Directory]::CreateDirectory((Join-Path $folder 'reframe-host')) | Out-Null
foreach ($f in $hostFiles) { [IO.File]::Copy((Join-Path $HostDir $f), (Join-Path $folder "reframe-host\$f")) }
$sources = [ordered]@{
    'history_backend.py' = 'maintenance\history_backend.py'
    'resources\maintenance\windows\stop-managed.ps1' = 'maintenance\windows\stop-managed.ps1'
    'resources\maintenance\windows\release-files.ps1' = 'maintenance\windows\release-files.ps1'
    'LICENSE' = 'LICENSE'
}
foreach ($entry in $sources.GetEnumerator()) {
    $target = Join-Path $folder $entry.Key
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    [IO.File]::Copy((Join-Path $root $entry.Value), $target)
}

# The payload check (inspect-payload.mjs's part that still applies).
function Same([string]$A, [string]$B) { (Get-FileHash -LiteralPath $A).Hash -ceq (Get-FileHash -LiteralPath $B).Hash }
$exe = Join-Path $folder 'reframe.exe'
$fileVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if ($fileVersion -cne $Version) { throw "reframe.exe has file version '$fileVersion', not $Version" }
if (Test-Path -LiteralPath (Join-Path $folder 'Reframe.Shell.exe')) { throw 'Reframe.Shell.exe must not ship beside reframe.exe' }
foreach ($entry in $sources.GetEnumerator()) { if (-not (Same (Join-Path $folder $entry.Key) (Join-Path $root $entry.Value))) { throw "Packaged $($entry.Key) differs from its source" } }
foreach ($f in $hostFiles) { if (-not (Same (Join-Path $folder "reframe-host\$f") (Join-Path $HostDir $f))) { throw "Packaged host file $f differs" } }
foreach ($f in $terminal) { if (-not (Test-Path -LiteralPath (Join-Path $folder $f))) { throw "Terminal control $f missing" } }
if (@(Get-ChildItem -LiteralPath $folder -Recurse -File -Include 'app.asar', '*.pak', 'electron*.dll').Count) { throw 'Electron files in the shell package' }

# Not just "tar": Git's GNU tar can come first on the PATH, and it does not make zip files.
& (Join-Path $env:SystemRoot 'System32\tar.exe') -c -f $zip --format zip -C $PackageDirectory $name
if ($LASTEXITCODE) { throw "Zip failed: $LASTEXITCODE" }
$files = @(Get-ChildItem -LiteralPath $folder -Recurse -File)
[ordered]@{ version = $Version; folder = $folder; zip = $zip; files = $files.Count
    bytes = ($files | Measure-Object Length -Sum).Sum; zipSha256 = (Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()
    executableSha256 = (Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant() } | ConvertTo-Json
