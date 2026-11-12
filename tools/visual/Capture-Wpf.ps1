# Visual parity, WPF side (CI): runs `Reframe.Shell.exe --diag-capture` twice into one folder: on a profile with no
# host (state main-empty), and against the Rust PTY host with two cmd.exe sessions (Start-CaptureHost.ps1; states
# main-sessions and tabs). Each run saves its PNGs and adds itself to capture.json. The host is ended afterwards.
param(
    [Parameter(Mandatory)] [string] $Shell,
    [Parameter(Mandatory)] [string] $HostBin,
    [Parameter(Mandatory)] [string] $Out,
    [Parameter(Mandatory)] [string] $Scratch
)
$ErrorActionPreference = 'Stop'
$Shell = (Resolve-Path $Shell).Path
$HostBin = (Resolve-Path $HostBin).Path
New-Item -ItemType Directory -Force $Out, $Scratch | Out-Null
$Out = (Resolve-Path $Out).Path

function Invoke-Capture([string[]] $arguments) {
    $p = Start-Process -FilePath $Shell -ArgumentList $arguments -PassThru
    if (-not $p.WaitForExit(120000)) {
        Stop-Process -Id $p.Id -Force
        throw "the shell did not finish its captures: $arguments"
    }
    Write-Host "shell exited $($p.ExitCode): $arguments"
}

Invoke-Capture @('--profile', "`"$(Join-Path $Scratch 'empty')`"", '--diag-capture', "`"$Out`"")

$profileDir = Join-Path $Scratch 'sessions'
$hostPid = (& (Join-Path $PSScriptRoot 'Start-CaptureHost.ps1') -HostBin $HostBin -ProfileDir $profileDir | Select-Object -Last 1)
Write-Host "host pid $hostPid"
try {
    Invoke-Capture @('--profile', "`"$profileDir`"", '--take-over', '--diag-capture', "`"$Out`"")
} finally {
    & taskkill /PID $hostPid /T /F | Out-Null
}
Get-ChildItem $Out | ForEach-Object { Write-Host "  $($_.Name) $($_.Length)" }
Get-Content (Join-Path $Out 'capture.json')
