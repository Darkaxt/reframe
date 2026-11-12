# Visual parity, WPF side (CI): starts the Rust PTY host (CI artifact reframe-host) for a new profile and creates two
# cmd.exe sessions over its pipe that print deterministic text, then detaches, so that `reframe-shell --diag-capture`
# has sessions with terminal content to show. Leaves the host running; prints its process id last.
# Based on wpf/tests/sandbox-smoke/guest/Start-Host.ps1 (docs/ptyhost-protocol.md: hello, spawn, spawned).
param(
    [Parameter(Mandatory)] [string] $HostBin,
    [Parameter(Mandatory)] [string] $ProfileDir
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
$prompt = '$G '
$sessions = @(
    @{ id = 'visual-1'; text = 'echo Reframe visual reference: session one& echo The quick brown fox jumps over the lazy dog 0123456789& ver' },
    @{ id = 'visual-2'; text = 'echo Reframe visual reference: session two& echo [ok] build passed  [warn] 2 warnings  [err] none' }
)
$instance = 0
foreach ($s in $sessions) {
    $instance++
    $spawn = [ordered]@{ type = 'spawn'; sessionId = $s.id; instance = $instance; file = 'C:\Windows\System32\cmd.exe'
        args = "/k prompt $prompt& $($s.text)"; cwd = $ProfileDir; env = $envMap; cols = 120; rows = 36 }
    $w.WriteLine(($spawn | ConvertTo-Json -Compress -Depth 4))
    $spawned = $false
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $line = $r.ReadLine(); if ($null -eq $line) { break }
        if ($line -match '"type":"spawned"') { $spawned = $true; break }
        if ($line -match '"type":"spawn-failed"') { Write-Host $line; break }
    }
    if (-not $spawned) { throw "session $($s.id) did not spawn" }
}
$c.Dispose()
Write-Output $p.Id
