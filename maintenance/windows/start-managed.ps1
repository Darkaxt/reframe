#Requires -Version 7.4
# Installed behind the existing no-console VBS shortcut. The requested GUI is visible.
param([Parameter(Mandatory)][string]$ActivePath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-files.ps1')
trap {
    # The VBS entry has no console. Retain a content-free actionable launch result.
    Write-AtomicJson (Join-Path (Split-Path -Parent $ActivePath) 'launch-failure.json') @{
        status = 'launch_failed'; error = $_.Exception.Message; at = [DateTime]::UtcNow.ToString('o')
    }
    throw $_
}
$active = Get-Content -LiteralPath $ActivePath -Raw | ConvertFrom-Json
Test-ReleaseFiles (Split-Path -Parent $active.executable) $active.files
Assert-Hash $active.executable $active.executableSha256
Assert-Hash $active.helper.helper $active.helper.helperSha256
Assert-PlainPath $active.runtime
if ([IO.Path]::GetPathRoot($active.runtime) -ine 'D:\') { throw 'Persistent runtime must be on D:' }
[IO.Directory]::CreateDirectory($active.runtime) | Out-Null
# Adopted clients (adopt-client.ps1): adopt a newer qualified client and delete unused copies first. A failed
# adoption never blocks the launch; the current record (verified by hash below) still applies.
$adoption = $null
if ($active.PSObject.Properties.Name -contains 'clientAdoption') {
    . (Join-Path $PSScriptRoot 'client-adoption.ps1')
    try { & (Join-Path $PSScriptRoot 'adopt-client.ps1') -InstallRoot (Split-Path -Parent $ActivePath) | Out-Null }
    catch {
        Write-AtomicJson (Join-Path $active.runtime 'client-adoption-failure.json') @{
            status = 'adoption_failed'; error = $_.Exception.Message; at = [DateTime]::UtcNow.ToString('o')
        }
    }
    if (Test-Path -LiteralPath $active.clientAdoption.record) { $adoption = Get-AdoptedClients $active.clientAdoption.record }
}
if (-not $adoption) {
    # No adoption yet: the in-place ChatGPT client bound by the pipeline's active.json.
    $cli = Get-Content -LiteralPath $active.managedActive -Raw | ConvertFrom-Json
    Assert-Hash $active.managedBinary $cli.sha256
}
$state = Get-Content -LiteralPath (Join-Path $active.profile 'state.json') -Raw | ConvertFrom-Json
if ($state.preferences.claudeBin -cne $active.managedBinary) { throw 'Managed CLI selection changed; inspect before launch' }
$values = @{
    TEMP = $active.runtime; TMP = $active.runtime; REFRAME_RUNTIME = $active.runtime
    REFRAME_USER_DATA = $active.profile; REFRAME_CLAUDE_BIN = $active.managedBinary
    REFRAME_ANTHROPIC_CLAUDE_BIN = $null
    REFRAME_CLAUDE_HOME = $active.claudeHome; CLAUDE_CONFIG_DIR = $null
    REFRAME_HISTORY_PYTHON = $active.helper.python; REFRAME_HISTORY_HELPER = $active.helper.helper
    REFRAME_HISTORY_HELPER_SHA256 = $active.helper.helperSha256
    REFRAME_CLIENT_ADOPTION = $null
    # PTY hosts run from versioned copies here, so removing a release folder never removes a running host's files.
    REFRAME_PTYHOST_ROOT = Join-Path (Split-Path -Parent $ActivePath) 'ptyhost'
    REFRAME_APP_ID = $null; REFRAME_APP_SHORTCUT = $null; REFRAME_APP_LAUNCHER = $null
}
# Toast clicks: a fixed app id per profile and a Start menu shortcut (kept by the app) that runs this profile's VBS
# launcher, the one the desktop shortcut runs. Without that launcher the app keeps its executable-path id.
$profileName = Split-Path -Leaf $active.profile
$launcher = Join-Path (Split-Path -Parent $ActivePath) "maintenance\start-$($profileName.ToLowerInvariant()).vbs"
if ($profileName -match '^[A-Za-z0-9-]+$' -and (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    $values.REFRAME_APP_ID = "Darkaxt.Reframe.$profileName"
    $values.REFRAME_APP_SHORTCUT = Join-Path ([Environment]::GetFolderPath('Programs')) "Reframe - $profileName.lnk"
    $values.REFRAME_APP_LAUNCHER = $launcher
}
if ($adoption) {
    # New sessions start on the adopted copies; the app re-reads the record (by hash) for each new session.
    $values.REFRAME_CLAUDE_BIN = $adoption.chatgpt.binary
    $values.REFRAME_ANTHROPIC_CLAUDE_BIN = $adoption.anthropic.binary
    $values.REFRAME_CLIENT_ADOPTION = $active.clientAdoption.record
} elseif ($active.PSObject.Properties.Name -contains 'providerBinaries') {
    $anthropic = $active.providerBinaries.anthropic
    Assert-PlainPath $anthropic.binary
    Assert-Hash $anthropic.binary $anthropic.sha256
    $values.REFRAME_ANTHROPIC_CLAUDE_BIN = $anthropic.binary
}
$saved = @{}
try {
    foreach ($key in $values.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        if ($null -eq $values[$key]) {
            # PowerShell converts ordinary $null to an empty string for this API.
            # Modern .NET preserves that empty variable; Claude then mkdir('') fails.
            [Environment]::SetEnvironmentVariable($key, [NullString]::Value, 'Process')
        } else { [Environment]::SetEnvironmentVariable($key, $values[$key], 'Process') }
    }
    if (Test-Path Env:CLAUDE_CONFIG_DIR) { throw 'Managed launch must remove the native configuration override' }
    $child = Start-Process -FilePath $active.executable -WorkingDirectory $active.workingDirectory -WindowStyle Normal -PassThru
    Write-AtomicJson (Join-Path $active.runtime 'last-launch.json') @{
        status = 'started_verification_pending'; pid = $child.Id; startTime = $child.StartTime.ToUniversalTime().ToString('o')
        executable = $active.executable; executableSha256 = $active.executableSha256
        activeSha256 = Get-Sha256 $ActivePath; at = [DateTime]::UtcNow.ToString('o')
        configOverridePresent = Test-Path Env:CLAUDE_CONFIG_DIR
        anthropicBinary = [Environment]::GetEnvironmentVariable('REFRAME_ANTHROPIC_CLAUDE_BIN', 'Process')
        chatgptBinary = [Environment]::GetEnvironmentVariable('REFRAME_CLAUDE_BIN', 'Process')
        clientAdoption = [Environment]::GetEnvironmentVariable('REFRAME_CLIENT_ADOPTION', 'Process')
    }
} finally {
    foreach ($key in $saved.Keys) {
        if ($null -eq $saved[$key]) { [Environment]::SetEnvironmentVariable($key, [NullString]::Value, 'Process') }
        else { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
    }
}
