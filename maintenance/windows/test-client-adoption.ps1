#Requires -Version 7.4
# Unit tests for client-adoption.ps1: candidate qualification and selection, and garbage-collection selection.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'client-adoption.ps1')
$failures = 0
function Check([string]$Name, [bool]$Condition) {
    if ($Condition) { return }
    $script:failures++
    Write-Host "FAIL: $Name"
}

$sha = 'a' * 64
function Entry([string]$Version = '2.1.322', [string]$Sha = $sha, [string]$Origin = 'pendingCandidate') {
    [pscustomobject]@{ origin = $Origin; version = $Version; sha256 = $Sha; directory = "X:\r\$Version"; binary = "X:\r\$Version\claude.exe"
        verification = "X:\r\$Version\verification.json"; verificationSha256 = 'v' * 64; candidate = "X:\r\$Version\candidate.json"
        manifest = 'X:\m.json'; manifestSha256 = 'm' * 64 }
}
function Verification([string]$Version = '2.1.322', [string]$Sha = $sha) {
    [pscustomobject]@{ schema = 'claudegpt.client_verification.v1'; qualified = $true; startupPassed = $true; realProxyRequestPassed = $true
        authorizationBehavior = [pscustomobject]@{ passed = $true }; payloadChecks = [pscustomobject]@{ a = $true; b = $true }
        version = $Version; candidateSha256 = $Sha }
}
function Candidate([string]$Version = '2.1.322', [string]$Sha = $sha) { [pscustomobject]@{ qualified = $true; candidateSha256 = $Sha; version = $Version } }
$hashes = @{ binary = $sha; verification = 'v' * 64; manifest = 'm' * 64 }

# Qualification
Check 'qualified candidate has no problems' (@(Get-ClientQualificationProblems (Entry) (Verification) (Candidate) $hashes).Count -eq 0)
$v = Verification; $v.qualified = $false
Check 'unqualified verification refused' (@(Get-ClientQualificationProblems (Entry) $v (Candidate) $hashes) -match 'qualified').Count -gt 0
$v = Verification; $v.realProxyRequestPassed = $false
Check 'failed request refused' (@(Get-ClientQualificationProblems (Entry) $v (Candidate) $hashes).Count -gt 0)
$v = Verification; $v.payloadChecks.b = $false
Check 'failed payload check refused' (@(Get-ClientQualificationProblems (Entry) $v (Candidate) $hashes) -match 'payload check b').Count -gt 0
$v = Verification; $v.authorizationBehavior.passed = $false
Check 'failed authorization probe refused' (@(Get-ClientQualificationProblems (Entry) $v (Candidate) $hashes).Count -gt 0)
Check 'missing verification refused' (@(Get-ClientQualificationProblems (Entry) $null (Candidate) $hashes).Count -gt 0)
Check 'executable hash mismatch refused' (@(Get-ClientQualificationProblems (Entry) (Verification) (Candidate) @{ binary = 'b' * 64; verification = 'v' * 64; manifest = 'm' * 64 }) -match 'executable hash').Count -gt 0
Check 'verification for another exe refused' (@(Get-ClientQualificationProblems (Entry) (Verification -Sha ('c' * 64)) (Candidate) $hashes).Count -gt 0)
Check 'verification for another version refused' (@(Get-ClientQualificationProblems (Entry) (Verification -Version '2.1.321') (Candidate) $hashes).Count -gt 0)
Check 'changed verification.json refused' (@(Get-ClientQualificationProblems (Entry) (Verification) (Candidate) @{ binary = $sha; verification = 'x' * 64; manifest = 'm' * 64 }).Count -gt 0)
Check 'manifest hash mismatch refused' (@(Get-ClientQualificationProblems (Entry) (Verification) (Candidate) @{ binary = $sha; verification = 'v' * 64; manifest = $null }).Count -gt 0)
Check 'candidate for another exe refused' (@(Get-ClientQualificationProblems (Entry) (Verification) (Candidate -Sha ('d' * 64)) $hashes).Count -gt 0)
Check 'missing candidate.json refused' (@(Get-ClientQualificationProblems (Entry) (Verification) $null $hashes).Count -gt 0)
$v = Verification; $v | Add-Member configFormatChanged $true
Check 'configFormatChanged refused' (@(Get-ClientQualificationProblems (Entry) $v (Candidate) $hashes) -match 'configFormatChanged').Count -gt 0
Check 'malformed version refused' (@(Get-ClientQualificationProblems (Entry -Version '2.1') (Verification -Version '2.1') (Candidate -Version '2.1') $hashes).Count -gt 0)

# Candidate list from the pipeline record
$pipeline = [pscustomobject]@{ version = '2.1.319'; sha256 = 'e' * 64; releaseDirectory = 'X:\r\319'; verification = 'X:\r\319\verification.json'
    pendingCandidate = [pscustomobject]@{ version = '2.1.322'; sha256 = $sha; directory = 'X:\r\322'; verification = 'X:\r\322\v.json'; verificationSha256 = 'v' * 64
        candidate = 'X:\r\322\candidate.json'; manifest = 'X:\m.json'; manifestSha256 = 'm' * 64 } }
$entries = @(Get-ClientCandidates $pipeline)
Check 'installed and pending entries read' ($entries.Count -eq 2 -and $entries[0].origin -ceq 'installed' -and $entries[1].binary -ceq 'X:\r\322\claude.exe' -and $entries[1].manifestSha256 -ceq ('m' * 64))
Check 'no pending candidate' (@(Get-ClientCandidates ([pscustomobject]@{ version = '2.1.319'; sha256 = 'e' * 64; releaseDirectory = 'X:\r' })).Count -eq 1)

# Selection
function WithProblems($e, $p) { $e | Add-Member -NotePropertyName problems -NotePropertyValue $p -PassThru }
$a = WithProblems (Entry -Version '2.1.319' -Origin 'installed') @()
$b = WithProblems (Entry -Version '2.1.322') @()
$c = WithProblems (Entry -Version '2.1.330') @('verification.qualified is not true')
Check 'newest qualified chosen' ((Select-ClientCandidate @($a, $b, $c) $null).version -ceq '2.1.322')
Check 'newer than current only' ((Select-ClientCandidate @($a, $b) '2.1.319').version -ceq '2.1.322')
Check 'nothing newer -> none' ($null -eq (Select-ClientCandidate @($a, $b) '2.1.322'))
Check 'older never chosen' ($null -eq (Select-ClientCandidate @($a) '2.1.322'))
Check 'numeric version order (2.1.40 < 2.1.322)' ((Select-ClientCandidate @((WithProblems (Entry -Version '2.1.40') @()), $b) $null).version -ceq '2.1.322')
Check 'unqualified newest skipped' ($null -eq (Select-ClientCandidate @($c) '2.1.322'))

# Garbage collection selection
$root = 'D:\Tools\Reframe\clients'
$old = "$root\anthropic\2.1.319-729f538ae4c5"; $cur = "$root\anthropic\2.1.322-11a05940383b"; $gpt = "$root\chatgpt\2.1.322-11a05940383b"
$oldGpt = "$root\chatgpt\2.1.319-729f538ae4c5"; $standalone = 'D:\Tools\claude-anthropic\clients\2.1.319-729f538ae4c5'
$folders = @($old, $cur, $gpt, $oldGpt, $standalone)
$keep = @("$cur\claude.exe", "$gpt\claude.exe")
$g = @(Select-ClientGarbage $folders $keep @() $false)
Check 'unused old copies collected' ($g.Count -eq 3 -and $g -contains $old -and $g -contains $oldGpt -and $g -contains $standalone)
Check 'current never collected' ($g -notcontains $cur -and $g -notcontains $gpt)
$g = @(Select-ClientGarbage $folders $keep @("$old\claude.exe".ToUpperInvariant()) $false)
Check 'running copy never collected (case-insensitive)' ($g -notcontains $old -and $g -contains $oldGpt)
$g = @(Select-ClientGarbage $folders $keep @("$cur\claude.exe") $false)
Check 'current and running never collected' ($g -notcontains $cur)
Check 'unknown running path -> nothing collected' (@(Select-ClientGarbage $folders $keep @() $true).Count -eq 0)
Check 'prefix sibling not confused' (@(Select-ClientGarbage @("$root\anthropic\2.1.3") @("$root\anthropic\2.1.32\claude.exe") @() $false).Count -eq 1)

if ($failures) { Write-Host "client adoption rules: $failures failed"; exit 1 }
Write-Host 'client adoption rules: all passed'
