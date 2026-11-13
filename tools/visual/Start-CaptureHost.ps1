# Visual parity, WPF side (CI): starts the Rust PTY host (CI artifact reframe-host) for a new profile and creates the
# visual fixtures' open sessions over its pipe (-Sessions "<session id>=<name>=<project>"), each running
# screenshots/demo-claude.mjs with that --name in %USERPROFILE%\projects\<project>, as e2e/visual-reference.spec.ts runs
# them for Electron (the same scenes, so the same terminal content), then detaches, so that
# `reframe-shell --diag-capture --diag-fixture` shows them. Leaves the host running; prints its process id last.
# Based on wpf/tests/sandbox-smoke/guest/Start-Host.ps1 (docs/ptyhost-protocol.md: hello, spawn, spawned).
param(
    [Parameter(Mandatory)] [string] $HostBin,
    [Parameter(Mandatory)] [string] $ProfileDir,
    [Parameter(Mandatory)] [string[]] $Sessions,
    [Parameter(Mandatory)] [string] $ClaudeHome
)
$ErrorActionPreference = 'Stop'

$hostDir = Join-Path $ProfileDir 'ptyhost'
New-Item -ItemType Directory -Force $hostDir | Out-Null
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$token = -join ($bytes | ForEach-Object { $_.ToString('x2') })
[IO.File]::WriteAllText((Join-Path $hostDir 'token'), $token)
$env:REFRAME_PTYHOST_DIR = $hostDir
$env:REFRAME_PTYHOST_VERSION = 'visual'
$p = Start-Process -FilePath (Join-Path $HostBin 'reframe-host.exe') -WindowStyle Hidden -PassThru
$desc = Join-Path $hostDir "hosts\$($p.Id).json"
$deadline = (Get-Date).AddSeconds(20)
while (-not (Test-Path $desc) -and (Get-Date) -lt $deadline -and -not $p.HasExited) { Start-Sleep -Milliseconds 200 }
if (-not (Test-Path $desc)) { throw "no host descriptor (host exited: $($p.HasExited))" }

$name = ((Get-Content $desc -Raw) | ConvertFrom-Json).pipe.Replace('\\.\pipe\', '')
$c = New-Object IO.Pipes.NamedPipeClientStream('.', $name, [IO.Pipes.PipeDirection]::InOut)
$c.Connect(5000)
$w = New-Object IO.StreamWriter($c, (New-Object Text.UTF8Encoding($false))); $w.NewLine = "`n"; $w.AutoFlush = $true
$r = New-Object IO.StreamReader($c, [Text.Encoding]::UTF8)
$w.WriteLine((@{ type = 'hello'; token = $token; protocol = 1 } | ConvertTo-Json -Compress))
Write-Host "welcome: $($r.ReadLine())"
$envMap = [ordered]@{}
foreach ($e in [Environment]::GetEnvironmentVariables().GetEnumerator() | Sort-Object Name) { $envMap[$e.Name] = [string]$e.Value }
New-Item -ItemType Directory -Force $ClaudeHome | Out-Null
$envMap['REFRAME_CLAUDE_HOME'] = (Resolve-Path $ClaudeHome).Path
$node = (Get-Command node).Source
$demo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\screenshots\demo-claude.mjs')).Path
$instance = 0
foreach ($entry in $Sessions) {
    $id, $name, $project = $entry -split '='
    $cwd = Join-Path $env:USERPROFILE "projects\$project"
    New-Item -ItemType Directory -Force $cwd | Out-Null
    $instance++
    $spawn = [ordered]@{ type = 'spawn'; sessionId = $id; instance = $instance; file = $node
        args = "`"$demo`" --name $name"; cwd = $cwd; env = $envMap; cols = 120; rows = 36 }
    $w.WriteLine(($spawn | ConvertTo-Json -Compress -Depth 4))
    $spawned = $false
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $line = $r.ReadLine(); if ($null -eq $line) { break }
        if ($line -match '"type":"spawned"') { $spawned = $true; break }
        if ($line -match '"type":"spawn-failed"') { Write-Host $line; break }
    }
    if (-not $spawned) { throw "session $id did not spawn" }
}
$c.Dispose()
Write-Output $p.Id
