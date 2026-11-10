# Launch the reviewed dedicated Claude CLI using subscription routing for this child only.
[CmdletBinding(PositionalBinding=$false)]
param(
 [string]$ActivePath='D:\Tools\Reframe\active.json',
 [Parameter(ValueFromRemainingArguments=$true)][string[]]$ClaudeArguments=@()
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$active=Get-Content -LiteralPath $ActivePath -Raw|ConvertFrom-Json
$binding=$active.providerBinaries.anthropic
if((Get-FileHash -LiteralPath $binding.binary -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binding.sha256){throw 'Dedicated Anthropic executable differs from its managed binding'}

# Same complete provider isolation as src/shared/providers.ts; never special-case the selected model.
$routing=@('ANTHROPIC_BASE_URL','ANTHROPIC_AUTH_TOKEN','ANTHROPIC_API_KEY','ANTHROPIC_MODEL','ANTHROPIC_DEFAULT_OPUS_MODEL','ANTHROPIC_DEFAULT_SONNET_MODEL','ANTHROPIC_DEFAULT_HAIKU_MODEL','ANTHROPIC_SMALL_FAST_MODEL','CLAUDE_CODE_SUBAGENT_MODEL','ANTHROPIC_CUSTOM_HEADERS','ANTHROPIC_CUSTOM_MODEL_OPTION','ANTHROPIC_CUSTOM_MODEL_OPTION_NAME','CLAUDE_CODE_USE_BEDROCK','CLAUDE_CODE_USE_VERTEX','CLAUDE_CODE_USE_FOUNDRY')
# Also clear additional named model aliases present in this environment/configuration.
$homePath=if($env:CLAUDE_CONFIG_DIR){$env:CLAUDE_CONFIG_DIR}else{$active.claudeHome}
$keys=@([Environment]::GetEnvironmentVariables('Process').Keys)
foreach($file in @((Join-Path $homePath 'settings.json'),(Join-Path (Get-Location).Path '.claude\settings.json'),(Join-Path (Get-Location).Path '.claude\settings.local.json'))){
 if(Test-Path -LiteralPath $file -PathType Leaf){
  $configuration=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json -AsHashtable
  if($configuration.ContainsKey('env') -and $configuration.env){$keys+=@($configuration.env.Keys)}
 }
}
$routing+=@($keys|Where-Object {$_ -match '^ANTHROPIC_DEFAULT_.+_MODEL$'})
$routing=@($routing|Sort-Object -Unique)
$route=@{};$saved=@{}
$runtime=Join-Path $active.runtime 'anthropic-terminal'
[IO.Directory]::CreateDirectory($runtime)|Out-Null
try{
 foreach($name in $routing+@('CLAUDE_CONFIG_DIR','TEMP','TMP')){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
 foreach($name in $routing){[Environment]::SetEnvironmentVariable($name,[NullString]::Value,'Process');$route[$name]=''}
 if(-not $env:CLAUDE_CONFIG_DIR){[Environment]::SetEnvironmentVariable('CLAUDE_CONFIG_DIR',[NullString]::Value,'Process')}
 $route.ANTHROPIC_BASE_URL='https://api.anthropic.com'
 $env:ANTHROPIC_BASE_URL=$route.ANTHROPIC_BASE_URL
 $env:TEMP=$runtime;$env:TMP=$runtime
 $settings=@{env=$route;apiKeyHelper='';model='sonnet'}|ConvertTo-Json -Compress -Depth 4
 & $binding.binary --settings $settings @ClaudeArguments
 $nativeExit=$LASTEXITCODE
}finally{
 foreach($name in $saved.Keys){
  $value=$saved[$name]
  if($null -eq $value){[Environment]::SetEnvironmentVariable($name,[NullString]::Value,'Process')}
  else{[Environment]::SetEnvironmentVariable($name,$value,'Process')}
 }
}
if($nativeExit){throw "Claude CLI exited with code $nativeExit"}
