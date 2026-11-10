#Requires -Version 7.4
# Maintenance stop for the exact idle app recorded by the controlled launcher.
param([Parameter(Mandatory)][string]$ActivePath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'release-files.ps1')
$active=Get-Content -LiteralPath $ActivePath -Raw|ConvertFrom-Json
if($active.PSObject.Properties.Name -notcontains 'quitCommand' -or $active.quitCommand -cne '--quit'){throw 'This installed release does not support managed quit.'}
Assert-Hash $active.executable $active.executableSha256
$launch=Get-Content -LiteralPath (Join-Path $active.runtime 'last-launch.json') -Raw|ConvertFrom-Json
if($launch.executable -cne $active.executable -or $launch.executableSha256 -cne $active.executableSha256){throw 'Launch provenance differs'}
$primary=Get-Process -Id $launch.pid -ErrorAction SilentlyContinue
if(-not $primary){return}
if($primary.Path -cne $active.executable -or $primary.StartTime.ToUniversalTime().Ticks -ne ([datetime]$launch.startTime).ToUniversalTime().Ticks){throw 'Recorded process identity differs'}
$statePath=Join-Path $active.profile 'state.json'
$state=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
if(@($state.sessions.PSObject.Properties|Where-Object {$_.Value.lifecycle -ne 'ended'}).Count){throw 'End active sessions before a maintenance stop.'}
$processes=@(Get-CimInstance Win32_Process)
# Only Claude work coupled to this installation blocks the stop (see Get-ReframeClients); the app's own
# processes are handled below, and a Claude CLI installed and launched elsewhere may keep running.
$coupled=@(Get-ReframeClients (Split-Path -Parent ([IO.Path]::GetFullPath($ActivePath)))|Where-Object {$_.ExecutablePath -cne $active.executable})
if($coupled.Count){throw "Claude work remains; preserve it: $($coupled|ConvertTo-Json -Compress)"}
$owned=@($primary);$ids=@($primary.Id)
do {
 $children=@($processes|Where-Object {$_.ParentProcessId -in $ids -and $_.ProcessId -notin $ids})
 foreach($child in $children){
  if($child.ExecutablePath -cne $active.executable){throw 'Unknown owned child; inspect its lifecycle first.'}
  $handle=Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
  if($handle){if($handle.Path -cne $active.executable -or $handle.StartTime.ToUniversalTime().Ticks -lt $primary.StartTime.ToUniversalTime().Ticks){throw 'Child identity differs'};$owned+=$handle}
  $ids+=$child.ProcessId
 }
}while($children.Count)
$saved=@{}
try {
 foreach($key in @('REFRAME_USER_DATA','TEMP','TMP')){$saved[$key]=[Environment]::GetEnvironmentVariable($key,'Process')}
 $env:REFRAME_USER_DATA=$active.profile;$env:TEMP=$active.runtime;$env:TMP=$active.runtime
 $command=Start-Process -FilePath $active.executable -ArgumentList '--quit' -WorkingDirectory $active.workingDirectory -WindowStyle Hidden -PassThru
 $command.WaitForExit()
 if($command.ExitCode -ne 0){throw "Quit command failed: $($command.ExitCode)"}
 foreach($process in $owned){$process.WaitForExit()}
 Write-AtomicJson (Join-Path $active.runtime 'last-stop.json') @{at=[datetime]::UtcNow.ToString('o');status='owned_processes_exited';executable=$active.executable;sha256=$active.executableSha256;profile=$active.profile;identities=@($owned|ForEach-Object {@{pid=$_.Id;startTime=$_.StartTime.ToUniversalTime().ToString('o')}})}
}finally{
 foreach($key in $saved.Keys){if($null -eq $saved[$key]){[Environment]::SetEnvironmentVariable($key,[NullString]::Value,'Process')}else{[Environment]::SetEnvironmentVariable($key,$saved[$key],'Process')}}
}
