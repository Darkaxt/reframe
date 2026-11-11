# Client adoption rules (pure functions; adopt-client.ps1 does the file work).
# Reframe adopts a patched Claude client that the Codex pipeline built and qualified by copying it, never patching
# or rebuilding it, into D:\Tools\Reframe\clients\<provider>\<version>-<sha12>\claude.exe. New sessions start on the
# adopted copy; running sessions keep the executable they started from, so adoption never needs an idle boundary.
Set-StrictMode -Version Latest

$script:ClientProviders = @('anthropic', 'chatgpt')

function Get-ClientProperty($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { $property.Value } else { $null }
}

# The pipeline's installed client and its pending candidate (client\active.json), as uniform entries.
function Get-ClientCandidates($PipelineActive) {
    $entries = @()
    $sources = @(
        @{ origin = 'installed'; entry = $PipelineActive; directory = 'releaseDirectory' }
        @{ origin = 'pendingCandidate'; entry = (Get-ClientProperty $PipelineActive 'pendingCandidate'); directory = 'directory' }
    )
    foreach ($source in $sources) {
        $e = $source.entry
        if (-not (Get-ClientProperty $e 'version') -or -not (Get-ClientProperty $e 'sha256')) { continue }
        $directory = Get-ClientProperty $e $source.directory
        if (-not $directory) { continue }
        $entries += [pscustomobject]@{
            origin = $source.origin
            version = [string](Get-ClientProperty $e 'version')
            sha256 = [string](Get-ClientProperty $e 'sha256')
            directory = [string]$directory
            binary = Join-Path $directory 'claude.exe'
            verification = [string](Get-ClientProperty $e 'verification')
            verificationSha256 = Get-ClientProperty $e 'verificationSha256'
            candidate = Get-ClientProperty $e 'candidate'
            manifest = Get-ClientProperty $e 'manifest'
            manifestSha256 = Get-ClientProperty $e 'manifestSha256'
        }
    }
    $entries
}

function ConvertTo-ClientVersion([string]$Text) {
    $parsed = $null
    if ($Text -cmatch '^\d+\.\d+\.\d+$' -and [version]::TryParse($Text, [ref]$parsed)) { return $parsed }
    $null
}

# Why an entry is not qualified (empty when it is). Inputs are what adopt-client.ps1 read and hashed:
#   $Verification / $Candidate: parsed verification.json / candidate.json ($Candidate may be $null when the entry has none)
#   $Hashes: binary, verification, manifest (actual sha256 of those files; manifest $null when the entry has none)
# A pipeline flag "configFormatChanged": true on the verification or candidate record refuses adoption: the new client
# would rewrite configuration that running older clients still read.
function Get-ClientQualificationProblems($Entry, $Verification, $Candidate, $Hashes) {
    $problems = @()
    if (-not (ConvertTo-ClientVersion $Entry.version)) { $problems += "version '$($Entry.version)' is not x.y.z" }
    if ($Entry.sha256 -cnotmatch '^[a-f0-9]{64}$') { $problems += 'entry sha256 malformed' }
    if ($null -eq $Verification) { return $problems + 'verification.json missing or unreadable' }
    if ((Get-ClientProperty $Verification 'schema') -cne 'claudegpt.client_verification.v1') { $problems += 'verification schema unknown' }
    foreach ($check in @('qualified', 'startupPassed', 'realProxyRequestPassed')) {
        if ((Get-ClientProperty $Verification $check) -cne $true) { $problems += "verification.$check is not true" }
    }
    $authorization = Get-ClientProperty $Verification 'authorizationBehavior'
    if ($authorization -and (Get-ClientProperty $authorization 'passed') -cne $true) { $problems += 'verification.authorizationBehavior.passed is not true' }
    $payload = Get-ClientProperty $Verification 'payloadChecks'
    if ($payload) {
        $names = if ($payload -is [Collections.IDictionary]) { @($payload.Keys) } else { @($payload.PSObject.Properties.Name) }
        foreach ($name in $names) { if ((Get-ClientProperty $payload $name) -cne $true) { $problems += "payload check $name is not true" } }
    }
    if ((Get-ClientProperty $Verification 'version') -cne $Entry.version) { $problems += 'verification is for another version' }
    if ((Get-ClientProperty $Verification 'candidateSha256') -cne $Entry.sha256) { $problems += 'verification is for another executable' }
    if ($Hashes.binary -cne $Entry.sha256) { $problems += 'executable hash differs from the pipeline record' }
    if ($Entry.verificationSha256 -and $Hashes.verification -cne $Entry.verificationSha256) { $problems += 'verification.json changed since it was recorded' }
    if ($Entry.manifestSha256 -and $Hashes.manifest -cne $Entry.manifestSha256) { $problems += 'deployment manifest hash differs' }
    if ($Entry.candidate) {
        if ($null -eq $Candidate) { $problems += 'candidate.json missing or unreadable' }
        else {
            if ((Get-ClientProperty $Candidate 'qualified') -cne $true) { $problems += 'candidate is not qualified' }
            if ((Get-ClientProperty $Candidate 'candidateSha256') -cne $Entry.sha256) { $problems += 'candidate.json is for another executable' }
            if ((Get-ClientProperty $Candidate 'version') -cne $Entry.version) { $problems += 'candidate.json is for another version' }
        }
    }
    foreach ($record in @($Verification, $Candidate)) {
        if ((Get-ClientProperty $record 'configFormatChanged') -eq $true) { $problems += 'flagged configFormatChanged' }
    }
    $problems
}

# The newest qualified entry (no problems) that is newer than the current adopted version, else $null.
function Select-ClientCandidate($Entries, [string]$CurrentVersion) {
    $current = if ($CurrentVersion) { ConvertTo-ClientVersion $CurrentVersion } else { $null }
    $eligible = @($Entries | Where-Object { @($_.problems).Count -eq 0 -and (ConvertTo-ClientVersion $_.version) } |
        Where-Object { $null -eq $current -or (ConvertTo-ClientVersion $_.version) -gt $current } |
        Sort-Object { ConvertTo-ClientVersion $_.version } -Descending)
    if ($eligible.Count) { $eligible[0] } else { $null }
}

# Versioned client folders to delete: those whose claude.exe is neither a current binding ($Keep, full exe paths) nor
# the executable of a running process ($Running, full paths). When a claude process's path could not be read
# ($RunningUnknown), nothing is deleted: that process could be running from any of them.
function Select-ClientGarbage([string[]]$Folders, [string[]]$Keep, [string[]]$Running, [bool]$RunningUnknown) {
    if ($RunningUnknown) { return }
    $normalise = { param($p) [IO.Path]::GetFullPath($p).TrimEnd('\') }
    $keepSet = @($Keep | Where-Object { $_ } | ForEach-Object { & $normalise $_ })
    $runningSet = @($Running | Where-Object { $_ } | ForEach-Object { & $normalise $_ })
    $garbage = @()
    foreach ($folder in $Folders) {
        $root = (& $normalise $folder) + '\'
        $used = @($keepSet + $runningSet | Where-Object { $_.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) })
        if (-not $used.Count) { $garbage += & $normalise $folder }
    }
    $garbage
}

# The adoption record's bindings, each verified by hash. Throws when the record is malformed or a binary changed.
function Get-AdoptedClients([string]$RecordPath) {
    $record = Get-Content -LiteralPath $RecordPath -Raw | ConvertFrom-Json
    if ($record.schema -cne 'reframe.client-adoption.v1') { throw "Unknown client adoption record: $RecordPath" }
    $clients = @{}
    foreach ($provider in $script:ClientProviders) {
        $binding = Get-ClientProperty $record.clients $provider
        if (-not $binding -or -not [IO.Path]::IsPathFullyQualified([string]$binding.binary)) { throw "Adopted $provider client missing" }
        $actual = (Get-FileHash -LiteralPath $binding.binary -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -cne $binding.sha256) { throw "Adopted $provider client differs from its record: $($binding.binary)" }
        $clients[$provider] = [pscustomobject]@{ binary = [string]$binding.binary; sha256 = [string]$binding.sha256; version = [string]$record.version }
    }
    $clients
}
