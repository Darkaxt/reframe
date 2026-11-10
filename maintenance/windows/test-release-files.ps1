#Requires -Version 7.4
# Isolated filesystem contract tests. Does not call the deployer, launch an app,
# disable its production idle checks, or touch an installed profile.
param([Parameter(Mandatory)][string]$Scratch)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-files.ps1')
if ([IO.Path]::GetPathRoot($Scratch) -ine 'D:\') { throw 'Native fixture I/O requires exact task-owned D: scratch' }
if (Test-Path -LiteralPath $Scratch) { throw 'Fixture destination already exists; inspect before another run' }
Assert-PlainPath $Scratch
[IO.Directory]::CreateDirectory((Join-Path $Scratch 'source\resources')) | Out-Null
[IO.File]::WriteAllText((Join-Path $Scratch 'source\app.exe'), 'fictional executable bytes')
[IO.File]::WriteAllText((Join-Path $Scratch 'source\resources\app.asar'), 'fictional payload bytes')
$source = Join-Path $Scratch 'source'
$files = @(Get-ChildItem -LiteralPath $source -File -Recurse | ForEach-Object {
    @{ file = [IO.Path]::GetRelativePath($source, $_.FullName); bytes = $_.Length; sha256 = Get-Sha256 $_.FullName }
})
$destination = Join-Path $Scratch 'release'
Copy-ReleaseFiles $source $destination $files
function Must-Throw([scriptblock]$Action, [string]$Name) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw "Expected rejection: $Name" }
}
Must-Throw { Copy-ReleaseFiles $source $destination $files } 'existing destination'
Must-Throw { Test-ReleaseFiles $source @(@{ file = '..\outside'; bytes = 0; sha256 = ('0' * 64) }) } 'path escape'
Must-Throw { Test-ReleaseFiles $source @($files[0], $files[0]) } 'duplicate'
$state = Join-Path $Scratch 'state.json'
$pointer = Join-Path $Scratch 'active.json'
Write-AtomicJson $state @{ layout = 'original'; preferences = 'owner' }
Write-AtomicJson $pointer @{ executable = 'known-good' }
$originalState = Get-Sha256 $state
$originalPointer = Get-Sha256 $pointer
$frozen = Save-RestoreFiles (Join-Path $Scratch 'frozen') @($state, $pointer)
Write-AtomicJson $state @{ layout = 'changed' }
Write-AtomicJson $pointer @{ executable = 'new' }
Restore-FrozenFiles $frozen
Assert-Hash $state $originalState
Assert-Hash $pointer $originalPointer
# Validate the entire restore set first: a corrupt second member must not
# partially restore the first. Leave all fixture bytes for transactional cleanup.
Write-AtomicJson $state @{ layout = 'must-remain' }
$before = Get-Sha256 $state
[IO.File]::WriteAllText($frozen[1].copy, 'corrupt')
Must-Throw { Restore-FrozenFiles $frozen } 'corrupt recovery set'
Assert-Hash $state $before
$candidate = @{ sourceCommit = ('f' * 40); archiveSha256 = ('a' * 64) }
$deferralPath = Join-Path $Scratch 'owner-deferral.json'
Write-AtomicJson $deferralPath @{ answer = 'Keep Anthropic verification pending' }
$qualification = @{
    chatgpt = $true; packaged = $true; history = $true; payload = $true
    sourceCommit = $candidate.sourceCommit; archiveSha256 = $candidate.archiveSha256
    anthropic = $true
}
Assert-ReleaseQualification $qualification $candidate
$qualification.anthropic = $false
Must-Throw { Assert-ReleaseQualification $qualification $candidate } 'missing explicit owner deferral'
$qualification.anthropicRouting = $true
$qualification.anthropicState = 'deferred_by_owner'
$qualification.anthropicDeferralReceipt = $deferralPath
$qualification.anthropicDeferralSha256 = Get-Sha256 $deferralPath
$qualification.limits = @('Real Anthropic subscription request remains unverified by owner choice.')
Assert-ReleaseQualification $qualification $candidate
$qualification.history = $false
Must-Throw { Assert-ReleaseQualification $qualification $candidate } 'unrelated failed check cannot be deferred'
$qualification.history = $true
Write-AtomicJson $deferralPath @{ answer = 'different decision' }
Must-Throw { Assert-ReleaseQualification $qualification $candidate } 'changed deferral receipt'
[IO.File]::WriteAllText((Join-Path $destination 'unexpected'), 'extra')
Must-Throw { Test-ReleaseFiles $destination $files } 'extra payload'
[IO.File]::WriteAllText((Join-Path $source 'app.exe'), 'changed')
Must-Throw { Test-ReleaseFiles $source $files } 'changed payload'
Write-AtomicJson (Join-Path $Scratch 'result.json') @{
    status = 'passed'; checked = @('exact copy', 'same-volume atomic pointer', 'frozen restore', 'restore prevalidation', 'explicit hash-bound provider deferral', 'unrelated failures remain blockers', 'path/duplicate/extra/changed rejection')
}
Get-Content -LiteralPath (Join-Path $Scratch 'result.json') -Raw
