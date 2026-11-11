#Requires -Version 7.4
<#
Adopt the newest qualified patched Claude client from the Codex pipeline, then delete unused client copies.

  adopt-client.ps1 [-InstallRoot D:\Tools\Reframe] [-PipelineActive <client\active.json>] [-GcOnly]

Reads the pipeline's client\active.json (installed client and pendingCandidate; never writes it). A candidate is adopted
when its verification.json and candidate.json say it is qualified, its executable, verification and manifest hashes
match the pipeline's record, it is newer than the current adopted client and it is not flagged configFormatChanged.
The executable is copied (never patched) to <InstallRoot>\clients\<provider>\<version>-<sha12>\claude.exe for both
providers, and <InstallRoot>\client-adoption.json (the receipt the pipeline reads) is rewritten atomically.

Garbage collection then deletes client folders under <InstallRoot>\clients\<provider> and the standalone CLI's
D:\Tools\claude-anthropic\clients that hold neither a current adopted client nor the executable of a running process.
Runs at every managed Reframe start (start-managed.ps1) and may be run by the pipeline after it qualifies a build.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = 'D:\Tools\Reframe',
    [string]$PipelineActive = 'D:\Tools\claudegpt-maintenance\client\active.json',
    [string[]]$ExtraClientRoots = @('D:\Tools\claude-anthropic\clients'),
    [switch]$GcOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-files.ps1')
. (Join-Path $PSScriptRoot 'client-adoption.ps1')

$recordPath = Join-Path $InstallRoot 'client-adoption.json'
$clientsRoot = Join-Path $InstallRoot 'clients'
$mutex = [Threading.Mutex]::new($false, 'Local\ReframeClientAdoption')
$owned = $false
try {
    try { $owned = $mutex.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'Another client adoption is still running' }
    $result = [ordered]@{ at = [DateTime]::UtcNow.ToString('o'); adopted = $null; candidates = @(); removed = @(); kept = @() }

    $record = if (Test-Path -LiteralPath $recordPath) { Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json } else { $null }
    if (-not $GcOnly) {
        $pipeline = Get-Content -LiteralPath $PipelineActive -Raw | ConvertFrom-Json
        $read = { param($path) if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } else { $null } }
        $hash = { param($path) if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) { Get-Sha256 $path } else { $null } }
        $entries = @(Get-ClientCandidates $pipeline)
        $current = if ($record) { [string]$record.version } else { $null }
        foreach ($entry in $entries) {
            # Hash the 300+ MB executable only for entries that could be adopted.
            $newer = (ConvertTo-ClientVersion $entry.version) -and (-not $current -or (ConvertTo-ClientVersion $entry.version) -gt (ConvertTo-ClientVersion $current))
            $problems = @(if (-not $newer) { 'not newer than the adopted client' } else {
                Get-ClientQualificationProblems $entry (& $read $entry.verification) (& $read $entry.candidate) @{
                    binary = & $hash $entry.binary; verification = & $hash $entry.verification; manifest = & $hash $entry.manifest
                }
            })
            $entry | Add-Member -NotePropertyName problems -NotePropertyValue $problems
            $result.candidates += [ordered]@{ origin = $entry.origin; version = $entry.version; sha256 = $entry.sha256; problems = $problems }
        }
        $chosen = Select-ClientCandidate $entries $current
        if ($chosen) {
            $clients = [ordered]@{}
            foreach ($provider in $script:ClientProviders) {
                $folder = Join-Path $clientsRoot "$provider\$($chosen.version)-$($chosen.sha256.Substring(0, 12))"
                $target = Join-Path $folder 'claude.exe'
                Assert-PlainPath $folder
                if (-not ((Test-Path -LiteralPath $target) -and (Get-Sha256 $target) -ceq $chosen.sha256)) {
                    [IO.Directory]::CreateDirectory($folder) | Out-Null
                    $temporary = "$target.$([guid]::NewGuid().ToString('N')).pending"
                    [IO.File]::Copy($chosen.binary, $temporary, $false)
                    Assert-Hash $temporary $chosen.sha256
                    [IO.File]::Move($temporary, $target, $true)
                }
                Assert-Hash $target $chosen.sha256
                $clients[$provider] = [ordered]@{ binary = $target; sha256 = $chosen.sha256 }
            }
            $record = [ordered]@{
                schema = 'reframe.client-adoption.v1'; version = $chosen.version; sha256 = $chosen.sha256
                sourceRelease = $chosen.directory; sourceBinary = $chosen.binary; origin = $chosen.origin
                verification = $chosen.verification; verificationSha256 = Get-Sha256 $chosen.verification
                candidate = $chosen.candidate; manifest = $chosen.manifest; manifestSha256 = $chosen.manifestSha256
                at = [DateTime]::UtcNow.ToString('o'); adoptedBy = $PSCommandPath; clients = $clients
                previous = if ($current) { [ordered]@{ version = $current; sha256 = [string]$record.sha256 } } else { $null }
            }
            Write-AtomicJson $recordPath $record
            $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
            $result.adopted = [ordered]@{ version = $chosen.version; sha256 = $chosen.sha256; clients = $clients }
        }
    }

    # Garbage collection: never the current adopted copies, never a folder a running process started from.
    $keep = @()
    if ($record) { foreach ($provider in $script:ClientProviders) { $keep += [string](Get-ClientProperty $record.clients $provider).binary } }
    $folders = @()
    foreach ($root in @($script:ClientProviders | ForEach-Object { Join-Path $clientsRoot $_ }) + $ExtraClientRoots) {
        if (Test-Path -LiteralPath $root) { $folders += @(Get-ChildItem -LiteralPath $root -Directory | ForEach-Object FullName) }
    }
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'claude.exe'")
    $running = @($processes | ForEach-Object ExecutablePath | Where-Object { $_ })
    $unknown = @($processes | Where-Object { -not $_.ExecutablePath }).Count -gt 0
    # Without a record nothing is current yet: keep everything until the first adoption.
    $garbage = @(if ($record) { Select-ClientGarbage $folders $keep $running $unknown })
    foreach ($folder in $garbage) {
        Assert-PlainPath $folder
        $files = @(Get-ChildItem -LiteralPath $folder -File -Recurse -Force)
        $bytes = [long]0; foreach ($f in $files) { $bytes += $f.Length }
        try {
            Remove-Item -LiteralPath $folder -Recurse -Force
            $result.removed += [ordered]@{ path = $folder; files = $files.Count; bytes = $bytes }
        } catch {
            # A process that started after the check holds it; the next run retries.
            $result.kept += [ordered]@{ path = $folder; reason = $_.Exception.Message }
        }
    }
    $result.kept += @($folders | Where-Object { $garbage -notcontains [IO.Path]::GetFullPath($_).TrimEnd('\') } | ForEach-Object { [ordered]@{ path = $_; reason = if ($unknown) { 'a claude process path is unreadable' } elseif (-not $record) { 'no adoption record yet' } else { 'current or running' } } })
    $result.runningUnknown = $unknown
    Write-AtomicJson (Join-Path $InstallRoot 'client-gc.json') $result
    [pscustomobject]$result
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
