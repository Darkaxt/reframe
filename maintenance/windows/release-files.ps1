# Shared file operations for the managed Windows release and its frozen rollback.
# No process stopping, recursive deletion, credential parsing or global configuration.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) {
    if ($null -eq $script:HashStamps) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
    # Launch only (Use-HashStamps): a file unchanged since it was last hashed (same size, write and creation time)
    # keeps that hash. Whoever can rewrite these files without changing those can rewrite the launcher too.
    $item = Get-Item -LiteralPath $Path -Force
    $key = [IO.Path]::GetFullPath($Path).ToLowerInvariant()
    $seen = $script:HashStamps[$key]
    if ($seen -and $seen.bytes -eq $item.Length -and $seen.write -eq $item.LastWriteTimeUtc.Ticks -and $seen.created -eq $item.CreationTimeUtc.Ticks) {
        $script:HashStampsUsed[$key] = $seen
        return $seen.sha256
    }
    $sha = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $script:HashStampsUsed[$key] = @{ sha256 = $sha; bytes = $item.Length; write = $item.LastWriteTimeUtc.Ticks; created = $item.CreationTimeUtc.Ticks }
    $sha
}

$script:HashStamps = $null
$script:HashStampsUsed = @{}

<# Start reusing the hashes recorded in $Path (missing or unreadable: everything is hashed again). #>
function Use-HashStamps([string]$Path) {
    $script:HashStamps = @{}
    $script:HashStampsUsed = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    try {
        $saved = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
        if ($saved.schema -ceq 'reframe.hash-stamps.v1') {
            foreach ($entry in $saved.files.GetEnumerator()) {
                $script:HashStamps[$entry.Key] = @{ sha256 = [string]$entry.Value.sha256; bytes = [long]$entry.Value.bytes; write = [long]$entry.Value.write; created = [long]$entry.Value.created }
            }
        }
    } catch { $script:HashStamps = @{} }
}

<# Keep the hashes this launch looked at, once all its checks passed. #>
function Save-HashStamps([string]$Path) {
    Write-AtomicJson $Path @{ schema = 'reframe.hash-stamps.v1'; files = $script:HashStampsUsed }
}

function Assert-Hash([string]$Path, [string]$Expected) {
    if ($Expected -cnotmatch '^[a-f0-9]{64}$' -or (Get-Sha256 $Path) -cne $Expected) {
        throw "Hash mismatch: $Path"
    }
}

function Assert-ReleaseQualification($Qualification, $Candidate) {
    foreach ($check in @('chatgpt', 'packaged', 'history', 'payload')) {
        if ($Qualification.$check -cne $true) { throw "Required qualification incomplete: $check" }
    }
    if ($Qualification.archiveSha256 -cne $Candidate.archiveSha256 -or
        $Qualification.sourceCommit -cne $Candidate.sourceCommit) { throw 'Qualification belongs to a different candidate/source' }
    if ($Qualification.anthropic -ceq $true) { return }
    if ($Qualification.anthropicState -ceq 'adopted_qualified_client') {
        # The Anthropic route runs a client adopted from the client pipeline (adopt-client.ps1), qualified there.
        if ($Qualification.anthropic -cne $false -or $Qualification.anthropicRouting -cne $true) { throw 'Adopted client qualification inconsistent' }
        Assert-Hash $Qualification.anthropicClientVerification $Qualification.anthropicClientVerificationSha256
        if (@($Qualification.limits).Count -eq 0) { throw 'The unverified native subscription request must remain explicit' }
        return
    }
    # A deferred account check stays visibly unverified. It does not block
    # unrelated verified functionality, and is never inferred from missing auth.
    if ($Qualification.anthropic -cne $false -or $Qualification.anthropicRouting -cne $true -or
        $Qualification.anthropicState -cne 'deferred_by_owner') { throw 'Anthropic qualification is neither verified nor explicitly deferred' }
    Assert-Hash $Qualification.anthropicDeferralReceipt $Qualification.anthropicDeferralSha256
    $deferral = Get-Content -LiteralPath $Qualification.anthropicDeferralReceipt -Raw | ConvertFrom-Json
    if ($deferral.answer -cne 'Keep Anthropic verification pending') { throw 'Owner deferral does not match this check' }
    if (@($Qualification.limits).Count -eq 0) { throw 'Deferred verification must remain explicit in delivery provenance' }
}

function Assert-PlainPath([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "Absolute path required: $Path" }
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse path refused: $current"
            }
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Test-ReleaseFiles([string]$Root, $Files) {
    Assert-PlainPath $Root
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('') + ''
    # One walk of the folder: no link anywhere below the root (so none on any member's path), then the members.
    $actual = @{}
    foreach ($item in ([IO.DirectoryInfo]$prefix).EnumerateFileSystemInfos('*', [IO.SearchOption]::AllDirectories)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse path refused: $($item.FullName)" }
        if ($item -is [IO.FileInfo]) { $actual[$item.FullName] = $item }
    }
    $expected = @{}
    foreach ($entry in $Files) {
        if ([IO.Path]::IsPathRooted($entry.file)) { throw 'Rooted payload member' }
        $path = [IO.Path]::GetFullPath((Join-Path $Root $entry.file))
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload path escape' }
        if ($expected.ContainsKey($path)) { throw 'Duplicate payload member' }
        $file = $actual[$path]
        if (-not $file) { throw "Missing payload member: $path" }
        if ($file.Length -ne $entry.bytes) { throw "Size mismatch: $path" }
        Assert-Hash $path $entry.sha256
        $expected[$path] = $true
    }
    foreach ($path in $actual.Keys) {
        if (-not $expected.ContainsKey($path)) { throw "Unexpected payload member: $path" }
    }
}

function Copy-ReleaseFiles([string]$Source, [string]$Destination, $Files) {
    Test-ReleaseFiles $Source $Files
    Assert-PlainPath $Destination
    if (Test-Path -LiteralPath $Destination) { throw 'Release destination already exists' }
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($entry in $Files) {
        $target = Join-Path $Destination $entry.file
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        [IO.File]::Copy((Join-Path $Source $entry.file), $target, $false)
    }
    Test-ReleaseFiles $Destination $Files
}

function Write-AtomicBytes([string]$Path, [byte[]]$Bytes) {
    Assert-PlainPath $Path
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).pending"
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($temporary, $Path, $true)
}

function Write-AtomicJson([string]$Path, $Value) {
    Write-AtomicBytes $Path ([Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 30) + "`n"))
}

function Save-RestoreFiles([string]$Directory, [string[]]$Paths) {
    if (Test-Path -LiteralPath $Directory) { throw 'Restore directory already exists' }
    Assert-PlainPath $Directory
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    $entries = @()
    foreach ($path in $Paths) {
        Assert-PlainPath $path
        $copy = Join-Path $Directory ("{0:D3}.bin" -f $entries.Count)
        [IO.File]::Copy($path, $copy, $false)
        $hash = Get-Sha256 $path
        Assert-Hash $copy $hash
        $entries += [ordered]@{ path = $path; copy = $copy; sha256 = $hash }
    }
    return ,$entries
}

function Restore-FrozenFiles($Entries) {
    # Validate the complete recovery set before changing its first member.
    foreach ($entry in $Entries) { Assert-PlainPath $entry.path; Assert-Hash $entry.copy $entry.sha256 }
    foreach ($entry in $Entries) {
        Write-AtomicBytes $entry.path ([IO.File]::ReadAllBytes($entry.copy))
        Assert-Hash $entry.path $entry.sha256
    }
}

function Get-ReframeClients([Parameter(Mandatory)][string]$Root) {
    # Clients coupled to this installation: the app, anything it started (its panes, node wrappers), and any
    # Claude client running from or configured into the installation folder (its clients, runtime, settings).
    # A Claude CLI installed and launched elsewhere without Reframe's settings reads and writes nothing an
    # activation replaces, so it may keep running. Likewise a Reframe running from another folder (a second
    # installation, a development build or a test fixture) is not this installation's app; a Reframe whose path
    # cannot be read still counts.
    $all = @(Get-CimInstance Win32_Process)
    $byId = @{}
    foreach ($process in $all) { $byId[[int]$process.ProcessId] = $process }
    $folder = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $isApp = {
        param($p)
        $p.Name -match '^reframe(\.exe)?$' -and
            (-not $p.ExecutablePath -or [IO.Path]::GetFullPath($p.ExecutablePath).StartsWith("$folder\", [StringComparison]::OrdinalIgnoreCase))
    }
    @($all | Where-Object {
        $p = $_
        $client = (& $isApp $p) -or $p.Name -match '^claude(\.exe)?$' -or
            ($p.Name -match '^(node|bun)(\.exe)?$' -and $p.CommandLine -match '(claude-code|claude[\\/]cli)')
        if (-not $client) { return $false }
        if (& $isApp $p) { return $true }
        foreach ($text in @($p.ExecutablePath, $p.CommandLine)) {
            if ($text -and $text.IndexOf($folder, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
        }
        # Started by the app: walk the parents, stopping at a reused id (a "parent" younger than its child).
        $child = $p
        while ($byId.ContainsKey([int]$child.ParentProcessId)) {
            $parent = $byId[[int]$child.ParentProcessId]
            if ($parent.ProcessId -eq $child.ProcessId -or $parent.CreationDate -gt $child.CreationDate) { break }
            if (& $isApp $parent) { return $true }
            $child = $parent
        }
        $false
    } | Select-Object ProcessId, ParentProcessId, CreationDate, ExecutablePath)
}

function Assert-ReframeIdle([Parameter(Mandatory)][string]$Root) {
    $clients = @(Get-ReframeClients $Root)
    if ($clients.Count) { throw "Clients still running; preserve them and retain the candidate: $($clients | ConvertTo-Json -Compress)" }
}

function Get-ReframeRequestTasks {
    # Native application automations require the explicit reservation as well.
    @(Get-ScheduledTask | Where-Object {
        $_.State -ne 'Disabled' -and (($_.Actions | ConvertTo-Json -Compress -Depth 8) -match '(claude|reframe|8317)')
    } | Sort-Object TaskPath, TaskName | ForEach-Object {
        [ordered]@{ name = $_.TaskPath + $_.TaskName; actions = $_.Actions; triggers = $_.Triggers }
    })
}
