param([Parameter(Mandatory)][string]$Scratch)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($Scratch)
if(-not $root.StartsWith('D:\Temp\claudegpt-reframe-workspace-',[StringComparison]::OrdinalIgnoreCase)){throw 'Exact task scratch required'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$fake=Join-Path $PSScriptRoot 'fixtures\anthropic-cli.ps1'
$active=Join-Path $root 'active.json';$result=Join-Path $root 'result.json';$homePath=Join-Path $root 'home'
[IO.Directory]::CreateDirectory($homePath)|Out-Null
@{env=@{ANTHROPIC_DEFAULT_FOURTH_MODEL='fictional-gateway-fourth'}}|ConvertTo-Json|Set-Content (Join-Path $homePath 'settings.json')
@{providerBinaries=@{anthropic=@{binary=$fake;sha256=(Get-FileHash $fake).Hash.ToLowerInvariant()}};claudeHome=$homePath;runtime=$root}|ConvertTo-Json -Depth 6|Set-Content $active
$env:REFRAME_TERMINAL_FIXTURE=$result
$saved=@{}
try{
 foreach($name in @('ANTHROPIC_BASE_URL','ANTHROPIC_DEFAULT_OPUS_MODEL','ANTHROPIC_DEFAULT_SONNET_MODEL','ANTHROPIC_DEFAULT_HAIKU_MODEL','ANTHROPIC_SMALL_FAST_MODEL','CLAUDE_CONFIG_DIR')){
  $saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')
  [Environment]::SetEnvironmentVariable($name,'fictional-gateway-value','Process')
 }
 [Environment]::SetEnvironmentVariable('CLAUDE_CONFIG_DIR',[NullString]::Value,'Process')
 foreach($model in @('opus','sonnet','haiku','exact-fourth-model')){
  & "$PSScriptRoot\windows\start-anthropic-session.ps1" -ActivePath $active --model $model --effort medium --dangerously-skip-permissions
  $actual=Get-Content $result -Raw|ConvertFrom-Json -AsHashtable
  if(($actual.arguments[2..($actual.arguments.Count-1)]-join '|') -cne "--model|$model|--effort|medium|--dangerously-skip-permissions"){throw 'Native arguments were changed'}
  foreach($name in $actual.settings.env.Keys){
   if($name -eq 'ANTHROPIC_BASE_URL'){if($actual.settings.env[$name] -cne 'https://api.anthropic.com' -or $actual.environment[$name] -cne 'https://api.anthropic.com'){throw 'Wrong direct route'}}
   elseif($actual.settings.env[$name] -cne '' -or $actual.environment[$name]){throw "Routing override survived: $name"}
  }
  if(-not $actual.settings.env.ContainsKey('ANTHROPIC_DEFAULT_FOURTH_MODEL') -or $actual.configOverride){throw 'Alias/config isolation incomplete'}
  if($env:ANTHROPIC_DEFAULT_OPUS_MODEL -cne 'fictional-gateway-value'){throw 'Parent environment was changed'}
 }
 @{passed=$true;models=@('opus','sonnet','haiku','exact-fourth-model');allRoutingAliasesCleared=$true;parentEnvironmentRestored=$true;modelEffortPermissionArgumentsPreserved=$true}|ConvertTo-Json
}finally{
 foreach($name in $saved.Keys){$value=$saved[$name];if($null -eq $value){$value=[NullString]::Value};[Environment]::SetEnvironmentVariable($name,$value,'Process')}
}
