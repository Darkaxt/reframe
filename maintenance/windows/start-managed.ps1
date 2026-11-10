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
$cli = Get-Content -LiteralPath $active.managedActive -Raw | ConvertFrom-Json
Assert-Hash $active.managedBinary $cli.sha256
Assert-Hash $active.helper.helper $active.helper.helperSha256
Assert-PlainPath $active.runtime
if ([IO.Path]::GetPathRoot($active.runtime) -ine 'D:\') { throw 'Persistent runtime must be on D:' }
[IO.Directory]::CreateDirectory($active.runtime) | Out-Null
$state = Get-Content -LiteralPath (Join-Path $active.profile 'state.json') -Raw | ConvertFrom-Json
if ($state.preferences.claudeBin -cne $active.managedBinary) { throw 'Managed CLI selection changed; inspect before launch' }
$values = @{
    TEMP = $active.runtime; TMP = $active.runtime; REFRAME_RUNTIME = $active.runtime
    REFRAME_USER_DATA = $active.profile; REFRAME_CLAUDE_BIN = $active.managedBinary
    REFRAME_CLAUDE_HOME = $active.claudeHome; CLAUDE_CONFIG_DIR = $null
    REFRAME_HISTORY_PYTHON = $active.helper.python; REFRAME_HISTORY_HELPER = $active.helper.helper
    REFRAME_HISTORY_HELPER_SHA256 = $active.helper.helperSha256
}
$saved = @{}
try {
    foreach ($key in $values.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $values[$key], 'Process')
    }
    $child = Start-Process -FilePath $active.executable -WorkingDirectory $active.workingDirectory -WindowStyle Normal -PassThru
    Write-AtomicJson (Join-Path $active.runtime 'last-launch.json') @{
        status = 'started_verification_pending'; pid = $child.Id; startTime = $child.StartTime.ToUniversalTime().ToString('o')
        executable = $active.executable; executableSha256 = $active.executableSha256
        activeSha256 = Get-Sha256 $ActivePath; at = [DateTime]::UtcNow.ToString('o')
    }
} finally {
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
}
