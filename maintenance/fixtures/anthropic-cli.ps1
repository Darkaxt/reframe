param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Forwarded)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$settings=$Forwarded[1]|ConvertFrom-Json -AsHashtable
$route=@{}
foreach($name in $settings.env.Keys){$route[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
@{arguments=$Forwarded;settings=$settings;environment=$route;configOverride=[Environment]::GetEnvironmentVariable('CLAUDE_CONFIG_DIR','Process');scratch=$env:TEMP}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $env:REFRAME_TERMINAL_FIXTURE
$global:LASTEXITCODE=0
