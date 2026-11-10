#Requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$Receipt,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ReceiptSha256
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-files.ps1')
Assert-Hash $Receipt $ReceiptSha256
$r = Get-Content -LiteralPath $Receipt -Raw | ConvertFrom-Json
if ($r.schema -cne 'claudegpt.reframe-rollback.v1') { throw 'Unsupported rollback contract' }
$mutex = [Threading.Mutex]::new($false, 'Local\ClaudeGPTReframeDeployment')
$owned = $false
try {
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { throw 'Interrupted operation; inspect before restore' }
    if (-not $owned) { throw 'Another activation/restore owns the mutex' }
    Assert-ReframeIdle
    Assert-Hash $r.previousExecutable $r.previousExecutableSha256
    Test-ReleaseFiles (Split-Path -Parent $r.previousExecutable) $r.previousPayload
    foreach ($entry in $r.protected) { Assert-Hash $entry.path $entry.sha256 }
    Restore-FrozenFiles $r.files
    Start-Process -FilePath "$env:SystemRoot\System32\wscript.exe" -ArgumentList @('//B', '//Nologo', ('"' + $r.launcher + '"')) -WindowStyle Hidden
    Write-AtomicJson (Join-Path (Split-Path -Parent $Receipt) 'restore-result.json') @{
        status = 'restarted_recovery_verification_pending'; receiptSha256 = $ReceiptSha256
        executable = $r.previousExecutable; executableSha256 = $r.previousExecutableSha256
        at = [DateTime]::UtcNow.ToString('o')
    }
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
