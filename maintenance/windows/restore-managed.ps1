#Requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$Receipt,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ReceiptSha256,
    # Roll back with the sessions still running in the PTY host (after stop-managed.ps1 -KeepSessions): only the app
    # itself must be gone, and only the pointer and launcher are restored. state.json stays as the newer release left it
    # (the shell writes reframe's schema), so the restored release takes the live sessions over.
    [switch]$KeepSessions
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-files.ps1')
Assert-Hash $Receipt $ReceiptSha256
$r = Get-Content -LiteralPath $Receipt -Raw | ConvertFrom-Json
# The installation folder: <root>\releases\<version>\reframe.exe.
$installRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $r.previousExecutable))
if ($r.schema -cne 'claudegpt.reframe-rollback.v1') { throw 'Unsupported rollback contract' }
$mutex = [Threading.Mutex]::new($false, 'Local\ClaudeGPTReframeDeployment')
$owned = $false
try {
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { throw 'Interrupted operation; inspect before restore' }
    if (-not $owned) { throw 'Another activation/restore owns the mutex' }
    if ($KeepSessions) {
        $folder = [IO.Path]::GetFullPath($installRoot).TrimEnd('')
        $apps = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -ieq 'reframe.exe' -and (-not $_.ExecutablePath -or
            [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith("$folder\", [StringComparison]::OrdinalIgnoreCase)) })
        if ($apps.Count) { throw "Reframe still runs; quit it with its sessions kept first: $(@($apps.ProcessId) -join ',')" }
    } else { Assert-ReframeIdle $installRoot }
    Assert-Hash $r.previousExecutable $r.previousExecutableSha256
    Test-ReleaseFiles (Split-Path -Parent $r.previousExecutable) $r.previousPayload
    foreach ($entry in $r.protected) {
        # Claude CLI rotates its OAuth tokens during normal use: only presence and JSON validity, never the contents.
        if ($entry.path -like '*\.claude\.credentials.json' -and (Get-Sha256 $entry.path) -cne $entry.sha256) {
            $null = Get-Content -LiteralPath $entry.path -Raw | ConvertFrom-Json -ErrorAction Stop; continue
        }
        Assert-Hash $entry.path $entry.sha256
    }
    $files = @($r.files | Where-Object { -not $KeepSessions -or [IO.Path]::GetFileName($_.path) -cnotin @('state.json', 'state.json.bak') })
    Restore-FrozenFiles $files
    Start-Process -FilePath "$env:SystemRoot\System32\wscript.exe" -ArgumentList @('//B', '//Nologo', ('"' + $r.launcher + '"')) -WindowStyle Hidden
    Write-AtomicJson (Join-Path (Split-Path -Parent $Receipt) 'restore-result.json') @{
        status = 'restarted_recovery_verification_pending'; receiptSha256 = $ReceiptSha256; sessionsKept = [bool]$KeepSessions
        executable = $r.previousExecutable; executableSha256 = $r.previousExecutableSha256
        at = [DateTime]::UtcNow.ToString('o')
    }
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
