#Requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ManifestSha256
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-files.ps1')
Assert-Hash $Manifest $ManifestSha256
$m = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
if ($m.schema -cne 'claudegpt.reframe-deploy.v1') { throw 'Unsupported private deployment manifest' }
$mutex = [Threading.Mutex]::new($false, 'Local\ClaudeGPTReframeDeployment')
$owned = $false
$changed = $false
$rollback = $null
try {
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { throw 'Prior deployment interrupted; inspect its journal first' }
    if (-not $owned) { throw 'Another Reframe activation owns the mutex' }
    foreach ($name in @('deploy-managed.ps1', 'release-files.ps1', 'restore-managed.ps1', 'start-managed.ps1')) {
        $script = @($m.scripts | Where-Object { $_.file -ceq $name })
        if ($script.Count -ne 1) { throw "Missing or duplicate pinned release script: $name" }
        Assert-Hash (Join-Path $PSScriptRoot $name) $script[0].sha256
    }
    Assert-Hash $m.candidate $m.candidateSha256
    $candidate = Get-Content -LiteralPath $m.candidate -Raw | ConvertFrom-Json
    if ($candidate.status -cnotin @('qualified', 'qualified_with_deferred_anthropic') -or $candidate.sourceCommit -cnotmatch '^[a-f0-9]{40}$') { throw 'Candidate is not release-qualified/source-pinned' }
    Assert-Hash $m.qualification $m.qualificationSha256
    $q = Get-Content -LiteralPath $m.qualification -Raw | ConvertFrom-Json
    Assert-ReleaseQualification $q $candidate
    Assert-Hash $m.delivery $m.deliverySha256
    $delivery = Get-Content -LiteralPath $m.delivery -Raw | ConvertFrom-Json
    if ($delivery.sourceCommit -cne $candidate.sourceCommit -or $delivery.archiveSha256 -cne $candidate.archiveSha256 -or -not $delivery.retrievedVerified) { throw 'Published delivery not independently verified' }
    Test-ReleaseFiles (Split-Path -Parent $candidate.executable) $candidate.files
    Assert-Hash $candidate.archive $candidate.archiveSha256
    Assert-Hash $m.activePath $m.previousActiveSha256
    $previous = Get-Content -LiteralPath $m.activePath -Raw | ConvertFrom-Json
    Assert-Hash $previous.executable $previous.executableSha256
    $managed = Get-Content -LiteralPath $m.managedActive -Raw | ConvertFrom-Json
    Assert-Hash $previous.managedBinary $managed.sha256
    foreach ($file in $m.protected) { Assert-Hash $file.path $file.sha256 }
    if ($candidate.PSObject.Properties.Name -contains 'providerBinaries') {
        if ($m.PSObject.Properties.Name -notcontains 'providerBinaries') { throw 'Provider binary binding missing' }
        $binding = $m.providerBinaries.anthropic
        if ($binding.binary -cne $candidate.providerBinaries.anthropic.binary -or $binding.sha256 -cne $candidate.providerBinaries.anthropic.sha256) { throw 'Provider binary binding differs from candidate' }
        Assert-PlainPath $binding.binary
        Assert-Hash $binding.binary $binding.sha256
    }
    Assert-Hash $candidate.helper.helper $candidate.helper.helperSha256
    Assert-Hash $m.idleReservation $m.idleReservationSha256
    $idle = Get-Content -LiteralPath $m.idleReservation -Raw | ConvertFrom-Json
    foreach ($condition in @('clientsClosed', 'backgroundAndGoalsPaused', 'keepIdleUntilVerified')) {
        if ($idle.$condition -cne $true) { throw "No idle reservation for this activation: $condition" }
    }
    if ($idle.candidateSha256 -cne $m.candidateSha256) { throw 'Idle reservation belongs to another candidate' }
    $tasks = @(Get-ReframeRequestTasks)
    if ($tasks.Count) { throw "Enabled scheduled request producers need review/reservation before activation: $($tasks | ConvertTo-Json -Compress -Depth 8)" }
    Assert-ReframeIdle
    Assert-PlainPath $m.releaseDirectory
    if (Test-Path -LiteralPath $m.releaseDirectory) { throw 'Destination exists; resolve prior activation before another copy' }
    Assert-PlainPath $m.receiptDirectory
    if (Test-Path -LiteralPath $m.receiptDirectory) { throw 'Activation receipt already exists; resolve it first' }
    [IO.Directory]::CreateDirectory($m.receiptDirectory) | Out-Null
    # Defined recovery purpose: original launcher, pointer and durable app state.
    $paths = @($m.activePath, $m.launcherPath, (Join-Path $previous.profile 'state.json'))
    $stateBackup = Join-Path $previous.profile 'state.json.bak'
    if (Test-Path -LiteralPath $stateBackup) { $paths += $stateBackup }
    $frozen = Save-RestoreFiles (Join-Path $m.receiptDirectory 'frozen') $paths
    $rollback = [ordered]@{
        schema = 'claudegpt.reframe-rollback.v1'; status = 'prepared'; at = [DateTime]::UtcNow.ToString('o')
        previousExecutable = $previous.executable; previousExecutableSha256 = $previous.executableSha256
        files = $frozen; protected = $m.protected; launcher = $previous.launcher
        newExecutable = Join-Path $m.releaseDirectory 'reframe.exe'
        previousPayload = @(Get-ChildItem -LiteralPath (Split-Path -Parent $previous.executable) -File -Recurse | ForEach-Object {
            @{ file = [IO.Path]::GetRelativePath((Split-Path -Parent $previous.executable), $_.FullName); bytes = $_.Length; sha256 = Get-Sha256 $_.FullName }
        })
    }
    $rollbackPath = Join-Path $m.receiptDirectory 'rollback.json'
    Write-AtomicJson $rollbackPath $rollback
    # Freeze the recovery implementation beside its data before changing live pointers.
    foreach ($name in @('restore-managed.ps1', 'release-files.ps1')) {
        [IO.File]::Copy((Join-Path $PSScriptRoot $name), (Join-Path $m.receiptDirectory $name), $false)
    }
    Write-AtomicJson (Join-Path $m.receiptDirectory 'activation.json') @{ status = 'prepared'; manifest = $Manifest; manifestSha256 = $ManifestSha256; rollbackSha256 = Get-Sha256 $rollbackPath }
    Copy-ReleaseFiles (Split-Path -Parent $candidate.executable) $m.releaseDirectory $candidate.files
    Assert-PlainPath $m.launcherDirectory
    if (Test-Path -LiteralPath $m.launcherDirectory) { throw 'Launcher destination already exists; inspect prior activation' }
    [IO.Directory]::CreateDirectory($m.launcherDirectory) | Out-Null
    foreach ($name in @('start-managed.ps1', 'release-files.ps1')) {
        [IO.File]::Copy((Join-Path $PSScriptRoot $name), (Join-Path $m.launcherDirectory $name), $false)
        Assert-Hash (Join-Path $m.launcherDirectory $name) (Get-Sha256 (Join-Path $PSScriptRoot $name))
    }
    Assert-ReframeIdle
    foreach ($file in $m.protected) { Assert-Hash $file.path $file.sha256 }
    $active = [ordered]@{
        schema = 'claudegpt.reframe.v2'; version = $candidate.version; status = 'activation_verification_pending'
        executable = Join-Path $m.releaseDirectory 'reframe.exe'; executableSha256 = $candidate.executableSha256
        sourceCommit = $candidate.sourceCommit; archiveSha256 = $candidate.archiveSha256; files = $candidate.files
        profile = $previous.profile; runtime = $previous.runtime; managedBinary = $previous.managedBinary
        managedActive = $m.managedActive; claudeHome = $m.claudeHome; workingDirectory = $m.workingDirectory
        helper = $candidate.helper; launcher = $previous.launcher; qualification = $m.qualification
        delivery = $m.delivery; rollback = $rollbackPath; rollbackSha256 = Get-Sha256 $rollbackPath
        anthropicLiveVerified = $q.anthropic; verificationLimits = $q.limits
        at = [DateTime]::UtcNow.ToString('o')
    }
    if ($candidate.PSObject.Properties.Name -contains 'providerBinaries') {
        $active['providerBinaries'] = $m.providerBinaries
    }
    $installedLauncher = Join-Path $m.launcherDirectory 'start-managed.ps1'
    $launcherBody = "#Requires -Version 7.4`n& '$($installedLauncher.Replace("'", "''"))' -ActivePath '$($m.activePath.Replace("'", "''"))'`n"
    $changed = $true
    Write-AtomicBytes $m.launcherPath ([Text.Encoding]::UTF8.GetBytes($launcherBody))
    Write-AtomicJson $m.activePath $active
    Start-Process -FilePath "$env:SystemRoot\System32\wscript.exe" -ArgumentList @('//B', '//Nologo', ('"' + $previous.launcher + '"')) -WindowStyle Hidden
    Write-AtomicJson (Join-Path $m.receiptDirectory 'activation.json') @{
        status = 'started_verification_pending'; manifest = $Manifest; manifestSha256 = $ManifestSha256
        activeSha256 = Get-Sha256 $m.activePath; rollback = $rollbackPath; rollbackSha256 = Get-Sha256 $rollbackPath
    }
} catch {
    $failure = $_.ToString()
    if ($rollback -and $changed) {
        Write-AtomicJson (Join-Path $m.receiptDirectory 'activation-failure.json') @{
            status = 'failed'; error = $failure; rollback = $rollbackPath; rollbackSha256 = Get-Sha256 $rollbackPath
        }
        try {
            # Never kill a child/consumer to make rollback possible. A live GUI
            # must exit through its native lifecycle before the frozen restore.
            Assert-ReframeIdle
            Restore-FrozenFiles $rollback.files
            Start-Process -FilePath "$env:SystemRoot\System32\wscript.exe" -ArgumentList @('//B', '//Nologo', ('"' + $rollback.launcher + '"')) -WindowStyle Hidden
            Write-AtomicJson (Join-Path $m.receiptDirectory 'restore-result.json') @{
                status = 'restarted_recovery_verification_pending'; error = $failure
                executable = $rollback.previousExecutable; executableSha256 = $rollback.previousExecutableSha256
            }
        } catch {
            Write-AtomicJson (Join-Path $m.receiptDirectory 'restore-result.json') @{
                status = 'restore_requires_owned_idle_or_repair'; activationError = $failure; restoreError = $_.ToString()
            }
        }
    }
    throw $failure
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
