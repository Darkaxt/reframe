#Requires -Version 7.4
<#
Packaged smoke of the WPF release (S14), in place of the Electron Playwright specs the deploy ran before the cutover.
Runs the exact package through the managed scripts on a throwaway installation inside -Scratch (never the real one):

  1. a managed installation like D:\Tools\Reframe (active.json, releases\<v> with the package and reframe-config.json,
     an Electron-written version 1 state.json, a fake claude binding, a fake history helper); no start-<profile>.vbs, so
     no app id, Start menu shortcut or login entry is touched, and notifications are off;
  2. start-managed.ps1: the launch receipt the deploy verifies, the process is the release's reframe.exe with the
     release's file version, a responsive main window, and the Rust host started from the installation's ptyhost root;
  3. stop-managed.ps1 -KeepSessions: reframe.exe --quit, all owned processes exit, last-stop.json; the preferences are
     unchanged and state.json still reads;
  4. a direct start (no launcher variables: reframe-config.json beside the executable gives the profile), then
     reframe.exe --quit for that profile ends it.

Run it on a hidden desktop (Invoke-OnHiddenDesktop.ps1) or a CI runner. Writes <Scratch>\smoke-result.json and exits
non-zero on any failure. Only processes running from -Scratch are ever stopped (left over by a failed run).
#>
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Scratch,
    [string]$Version,
    # The managed scripts to test (default: these, beside this script).
    [string]$Scripts = $PSScriptRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $Scripts 'release-files.ps1')
$Package = [IO.Path]::GetFullPath($Package); $Scratch = [IO.Path]::GetFullPath($Scratch)
if (-not $Version) { $Version = (Get-Item -LiteralPath (Join-Path $Package 'reframe.exe')).VersionInfo.FileVersion }
if (Test-Path -LiteralPath $Scratch) { throw "$Scratch exists; give the smoke a new folder" }
if ([IO.Path]::GetPathRoot($Scratch) -ine 'D:\') { throw 'start-managed.ps1 keeps the runtime on D:; use a D: scratch folder' }
$steps = [ordered]@{}
$result = [ordered]@{ schema = 'reframe.packaged-smoke.v1'; version = $Version; package = $Package; passed = $false; steps = $steps }
$resultPath = Join-Path $Scratch 'smoke-result.json'

function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$What) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) { if (& $Condition) { return }; Start-Sleep -Milliseconds 250 }
    throw "Timed out after $Seconds s: $What"
}
function Wait-Window([Diagnostics.Process]$Process) {
    if (-not $Process.WaitForInputIdle(60000)) { throw 'reframe.exe did not reach input idle' }
    Wait-Until { $Process.Refresh(); if ($Process.HasExited) { throw "reframe.exe exited ($($Process.ExitCode))" }; $Process.MainWindowHandle -ne 0 -and $Process.Responding } 90 'a responsive main window'
}
function Own-Processes { @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith("$Scratch\", [StringComparison]::OrdinalIgnoreCase) }) }

try {
    $install = Join-Path $Scratch 'install'
    $release = Join-Path $install "releases\$Version"
    $profile = Join-Path $install 'profiles\Smoke'
    $runtime = Join-Path $install 'runtime'
    $fake = Join-Path $Scratch 'fake'
    foreach ($d in @($profile, $runtime, $fake, (Join-Path $Scratch 'claude-home'), (Join-Path $Scratch 'work'))) { [IO.Directory]::CreateDirectory($d) | Out-Null }
    $claude = Join-Path $fake 'claude.cmd'
    [IO.File]::WriteAllText($claude, "@echo fake claude (packaged smoke): never a real one`r`n")
    $helper = Join-Path $fake 'thread_cleanup.py'
    [IO.File]::WriteAllText($helper, "# fake history helper`n")
    $managedActive = Join-Path $fake 'managed-active.json'
    Write-AtomicJson $managedActive @{ sha256 = Get-Sha256 $claude }
    # An Electron-written state (version 1, as reframe.exe saves it) with the managed claude chosen.
    Write-AtomicJson (Join-Path $profile 'state.json') ([ordered]@{
        version = 1; sessions = @{}; windows = @(@{ id = 'w0'; bounds = $null; maximized = $false; hidden = $false; layout = $null; dock = @() })
        recentFolders = @(); hiddenThreads = @()
        preferences = [ordered]@{ nameFromFolder = $false; language = 'en'; fontSize = 13; snapToGrid = $true; checkForUpdates = $false
            lookInWsl = $false; closeWindow = 'keep'; gpuAcceleration = $false; ptyHost = 'rust'; claudeBin = $claude }
    })
    $preferencesBefore = (Get-Content -LiteralPath (Join-Path $profile 'state.json') -Raw | ConvertFrom-Json -AsHashtable).preferences

    # The release as deploy-managed.ps1 installs it: the package plus reframe-config.json, hashed.
    Copy-Item -LiteralPath $Package -Destination $release -Recurse
    $history = [ordered]@{ python = $claude; helper = $helper; helperSha256 = Get-Sha256 $helper }
    [IO.File]::WriteAllText((Join-Path $release 'reframe-config.json'), ([ordered]@{
        userData = $profile; runtime = $runtime; claudeHome = (Join-Path $Scratch 'claude-home')
        claudeBin = [ordered]@{ path = $claude; sha256 = Get-Sha256 $claude }; history = $history
    } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $files = @(Get-ChildItem -LiteralPath $release -File -Recurse | ForEach-Object { @{ file = [IO.Path]::GetRelativePath($release, $_.FullName); bytes = $_.Length; sha256 = Get-Sha256 $_.FullName } })
    $exe = Join-Path $release 'reframe.exe'
    $activePath = Join-Path $install 'active.json'
    Write-AtomicJson $activePath ([ordered]@{
        schema = 'claudegpt.reframe.v2'; version = $Version; status = 'installed_verified'
        executable = $exe; executableSha256 = Get-Sha256 $exe; files = $files
        profile = $profile; runtime = $runtime; managedBinary = $claude; managedActive = $managedActive
        claudeHome = (Join-Path $Scratch 'claude-home'); workingDirectory = (Join-Path $Scratch 'work'); helper = $history
        quitCommand = '--quit'
    })
    $steps.install = 'ok'

    # Never a toast or a real coordinator from a smoke; nothing from the caller's own launcher.
    $env:REFRAME_DISABLE_NOTIFICATIONS = '1'
    foreach ($v in @('REFRAME_USER_DATA', 'REFRAME_APP_ID', 'REFRAME_APP_SHORTCUT', 'REFRAME_APP_LAUNCHER', 'REFRAME_RUST_HOST_DIR', 'REFRAME_E2E')) {
        [Environment]::SetEnvironmentVariable($v, [NullString]::Value, 'Process')
    }

    # 2. Managed start.
    $started = [DateTime]::UtcNow
    & (Join-Path $Scripts 'start-managed.ps1') -ActivePath $activePath
    $launch = Get-Content -LiteralPath (Join-Path $runtime 'last-launch.json') -Raw | ConvertFrom-Json
    if ($launch.executable -cne $exe -or $launch.executableSha256 -cne (Get-Sha256 $exe) -or $launch.configOverridePresent -ne $false -or
        $launch.chatgptBinary -cne $claude -or ([datetime]$launch.at).ToUniversalTime() -lt $started.AddSeconds(-1)) { throw "Launch receipt differs: $($launch | ConvertTo-Json -Compress)" }
    $process = Get-Process -Id $launch.pid
    if ($process.Path -cne $exe) { throw "Launched process is $($process.Path), not $exe" }
    if ($process.MainModule.FileVersionInfo.FileVersion -cne $Version) { throw "File version $($process.MainModule.FileVersionInfo.FileVersion), not $Version" }
    Wait-Window $process
    $steps.managedStart = @{ pid = $process.Id; window = [int64]$process.MainWindowHandle }
    $ptyRoot = Join-Path $install 'ptyhost'
    Wait-Until { @(Get-CimInstance Win32_Process -Filter "Name='reframe-host.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith("$ptyRoot\", [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0 } 60 "the Rust host from $ptyRoot"
    Wait-Until { @(Get-ChildItem -LiteralPath (Join-Path $profile 'ptyhost\hosts') -Filter '*.json' -ErrorAction SilentlyContinue).Count -gt 0 } 30 'the host descriptor in the profile'
    $steps.host = 'started from the installation ptyhost root'

    # 3. Managed stop, sessions kept (the hot swap's quit).
    & (Join-Path $Scripts 'stop-managed.ps1') -ActivePath $activePath -KeepSessions
    $stop = Get-Content -LiteralPath (Join-Path $runtime 'last-stop.json') -Raw | ConvertFrom-Json
    if ($stop.status -cne 'owned_processes_exited' -or -not $stop.sessionsKept) { throw "Stop receipt differs: $($stop | ConvertTo-Json -Compress)" }
    if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -eq $process.StartTime }) { throw 'reframe.exe still runs after --quit' }
    $state = Get-Content -LiteralPath (Join-Path $profile 'state.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($key in $preferencesBefore.Keys) {
        if (($state.preferences[$key] | ConvertTo-Json -Compress -Depth 10) -cne ($preferencesBefore[$key] | ConvertTo-Json -Compress -Depth 10)) { throw "Preference changed by the shell: $key" }
    }
    $steps.managedStop = @{ status = $stop.status; stateVersion = $state.version }

    # 4. Direct start: reframe-config.json beside the executable gives the profile; --quit for that profile ends it.
    $direct = Start-Process -FilePath $exe -WorkingDirectory (Join-Path $Scratch 'work') -PassThru
    Wait-Window $direct
    $env:REFRAME_USER_DATA = $profile
    try { $quit = Start-Process -FilePath $exe -ArgumentList '--quit' -WindowStyle Hidden -PassThru -Wait } finally { $env:REFRAME_USER_DATA = $null }
    if ($quit.ExitCode -ne 0) { throw "reframe.exe --quit exited $($quit.ExitCode)" }
    if (-not $direct.WaitForExit(30000)) { throw 'The directly started shell (profile from reframe-config.json) did not quit for that profile' }
    $steps.directStart = @{ quitExitCode = $quit.ExitCode; exitCode = $direct.ExitCode }
    $result.passed = $true
} catch {
    $result.error = $_.ToString()
    $result.at = $_.InvocationInfo.PositionMessage
} finally {
    # Anything of the smoke's own still running (a failed step, or a host before its idle exit) is the smoke's to end.
    $left = Own-Processes
    foreach ($p in $left) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    $result.leftoverProcesses = @($left | ForEach-Object { "$($_.Name) $($_.ProcessId)" })
    if (Test-Path -LiteralPath $Scratch) { Write-AtomicJson $resultPath $result }
}
$result | ConvertTo-Json -Depth 6
if (-not $result.passed) { exit 1 }
