# Native qualification fixture only; never changes the installed active pointer.
param([Parameter(Mandatory)][int]$PrimaryPid,[Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$Profile,[Parameter(Mandatory)][string]$Runtime,[Parameter(Mandatory)][string]$ActivePath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'release-files.ps1')
$process=Get-Process -Id $PrimaryPid
if(-not [string]::Equals($process.Path,[IO.Path]::GetFullPath($Executable),[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture process path differs'}
$Executable=$process.Path
if(-not $ActivePath.StartsWith((Split-Path -Parent $Profile)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture pointer must stay inside its task root'}
if(Test-Path -LiteralPath $ActivePath){throw 'Fixture pointer already exists'}
[IO.Directory]::CreateDirectory($Runtime)|Out-Null
$sha=Get-Sha256 $Executable
Write-AtomicJson (Join-Path $Runtime 'last-launch.json') @{pid=$PrimaryPid;executable=$Executable;executableSha256=$sha;startTime=$process.StartTime.ToUniversalTime().ToString('o')}
Write-AtomicJson $ActivePath @{executable=$Executable;executableSha256=$sha;quitCommand='--quit';profile=$Profile;runtime=$Runtime;workingDirectory=(Split-Path -Parent $Profile)}
