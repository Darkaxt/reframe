# Visual parity, WPF side (CI): runs `Reframe.Shell.exe --diag-capture --diag-fixture` once per visual fixture
# (tools/visual/fixtures) into one folder: "empty" without a host (main-empty, quota, quota-panel, about, settings-*), then "sessions" (main-sessions, grid-2x2, sidebar,
# sidebar-row-*, sidebar-search), "hidden" (hidden-threads, hidden-threads-component) and "tabs" (group, group-frame, collapsed, dock-strip, zoomed, tabs) against one Rust PTY host that runs the fixtures' open
# sessions with demo-claude (Start-CaptureHost.ps1). Each run saves its PNGs and adds itself to capture.json. The host
# is ended afterwards.
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
$fixtures = Join-Path $PSScriptRoot 'fixtures'

function Invoke-Capture([string[]] $arguments) {
    $p = Start-Process -FilePath $Shell -ArgumentList $arguments -PassThru
    if (-not $p.WaitForExit(180000)) {
        Stop-Process -Id $p.Id -Force
        throw "the shell did not finish its captures: $arguments"
    }
    Write-Host "shell exited $($p.ExitCode): $arguments"
}

function Fixture([string] $name) { "`"$(Join-Path $fixtures "$name.json")`"" }

Invoke-Capture @('--profile', "`"$(Join-Path $Scratch 'empty')`"", '--diag-capture', "`"$Out`"", '--diag-fixture', (Fixture 'empty'))

# The sessions the fixtures host (their tiles show terminals), by session id, with the name demo-claude draws and the project folder.
$open = foreach ($name in 'sessions', 'tabs') {
    $fixture = Get-Content (Join-Path $fixtures "$name.json") -Raw | ConvertFrom-Json
    foreach ($id in $fixture.hosted) {
        $s = $fixture.sessions | Where-Object { $_.id -eq $id }
        "$($s.id)=$($s.name)=$($s.project)"
    }
}
$profileDir = Join-Path $Scratch 'sessions'
$hostPid = (& (Join-Path $PSScriptRoot 'Start-CaptureHost.ps1') -HostBin $HostBin -ProfileDir $profileDir `
    -Sessions $open -ClaudeHome (Join-Path $Scratch 'claude-home') | Select-Object -Last 1)
Write-Host "host pid $hostPid"
try {
    foreach ($name in 'sessions', 'hidden', 'tabs') {
        Invoke-Capture @('--profile', "`"$profileDir`"", '--take-over', '--diag-capture', "`"$Out`"", '--diag-fixture', (Fixture $name))
    }
} finally {
    & taskkill /PID $hostPid /T /F | Out-Null
}
Get-ChildItem $Out | ForEach-Object { Write-Host "  $($_.Name) $($_.Length)" }
Get-Content (Join-Path $Out 'capture.json')
if (Test-Path (Join-Path $Out 'capture-error.txt')) { Get-Content (Join-Path $Out 'capture-error.txt') }
