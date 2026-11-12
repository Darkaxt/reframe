# Reframe WPF shell smoke, guest side: copies the host and the shell to guest disk, starts the PTY host and creates a
# cmd.exe session ("smoke-1") over its pipe. Runs as LocalSystem (sandbox.powershell) inside the disposable guest.
# Exit 0 only when the host is up and the session spawned.
$ErrorActionPreference = 'Stop'
$work = 'C:\Coordinator\Work'
$log = Join-Path $work 'start-host.log'
function Log($m) { "$(Get-Date -Format o) $m" | Out-File -FilePath $log -Append -Encoding utf8 }

# The profile stays on guest disk: the mapped Work folder supports neither ACLs nor change notifications.
$root = 'C:\ShellTest'
$profileDir = Join-Path $root 'profile'
$hostDir = Join-Path $profileDir 'ptyhost'
$hostBin = Join-Path $root 'host'
$shellBin = Join-Path $root 'shell'
try {
    New-Item -ItemType Directory -Force $hostDir, $hostBin, $shellBin | Out-Null
    Copy-Item 'C:\In\host\*' $hostBin -Recurse -Force
    Copy-Item 'C:\In\shell\*' $shellBin -Recurse -Force
    Log ('host files: ' + ((Get-ChildItem $hostBin -File).Name -join ', '))

    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $token = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    [IO.File]::WriteAllText((Join-Path $hostDir 'token'), $token)
    $env:REFRAME_PTYHOST_DIR = $hostDir
    $env:REFRAME_PTYHOST_VERSION = 'smoke'
    # SYSTEM's USERDOMAIN is the workgroup; reframe grants files to USERDOMAIN\USERNAME.
    $env:USERDOMAIN = 'NT AUTHORITY'
    $p = Start-Process -FilePath (Join-Path $hostBin 'reframe-host.exe') -WindowStyle Hidden -PassThru
    Log "host started pid $($p.Id)"
    $desc = Join-Path $hostDir "hosts\$($p.Id).json"
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $desc) -and (Get-Date) -lt $deadline -and -not $p.HasExited) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $desc)) {
        Log "no host descriptor (exited=$($p.HasExited) code=$(if ($p.HasExited) { '0x{0:X8}' -f $p.ExitCode }))"
        exit 2
    }
    $d = Get-Content $desc -Raw
    Log "descriptor: $d"

    # A minimal protocol client (docs/ptyhost-protocol.md): hello, spawn cmd.exe, wait for spawned, detach.
    $name = ($d | ConvertFrom-Json).pipe -replace '^\\\.\pipe\', ''
    $c = New-Object IO.Pipes.NamedPipeClientStream('.', $name, [IO.Pipes.PipeDirection]::InOut)
    $c.Connect(5000)
    $w = New-Object IO.StreamWriter($c, (New-Object Text.UTF8Encoding($false))); $w.NewLine = "`n"; $w.AutoFlush = $true
    $r = New-Object IO.StreamReader($c, [Text.Encoding]::UTF8)
    $w.WriteLine((@{ type = 'hello'; token = $token; protocol = 1 } | ConvertTo-Json -Compress))
    Log "welcome: $($r.ReadLine())"
    $envMap = [ordered]@{}
    foreach ($e in [Environment]::GetEnvironmentVariables().GetEnumerator() | Sort-Object Name) { $envMap[$e.Name] = [string]$e.Value }
    $spawn = [ordered]@{ type = 'spawn'; sessionId = 'smoke-1'; instance = 1; file = 'C:\Windows\System32\cmd.exe'
        args = '/k echo READY'; cwd = $root; env = $envMap; cols = 100; rows = 30 }
    $w.WriteLine(($spawn | ConvertTo-Json -Compress -Depth 4))
    $spawned = $false
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $line = $r.ReadLine(); if ($null -eq $line) { Log 'pipe closed'; break }
        Log "event: $($line.Substring(0, [Math]::Min(400, $line.Length)))"
        if ($line -match '"type":"spawned"') { $spawned = $true; break }
        if ($line -match '"type":"spawn-failed"') { break }
    }
    $c.Dispose()
    Log "client detached, spawned=$spawned"
    if (-not $spawned) { exit 3 }
    exit 0
} catch {
    Log "error: $_"
    exit 1
}
