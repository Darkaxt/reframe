#Requires -Version 7.4
param([Parameter(Mandatory)][ValidateSet('Install','Validate','Package')][string]$Mode,
 [Parameter(Mandatory)][string]$Scratch,[ValidateSet('Typecheck','Lint')][string]$FirstCheck='Typecheck',[string]$PackageDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$scratchPath=[IO.Path]::GetFullPath($Scratch)
if(!$scratchPath.StartsWith('D:\Temp\claudegpt-reframe-', [StringComparison]::OrdinalIgnoreCase)){throw 'Use exact task-owned D: native build scratch'}
New-Item -ItemType Directory -Force -Path $scratchPath|Out-Null
$names=@('TEMP','TMP','npm_config_cache','ELECTRON_CACHE','ELECTRON_BUILDER_CACHE','REFRAME_PACKAGE_DIR')
$saved=@{};foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try{
 $env:TEMP=$scratchPath;$env:TMP=$scratchPath;$env:npm_config_cache=Join-Path $scratchPath 'npm';$env:ELECTRON_CACHE=Join-Path $scratchPath 'electron';$env:ELECTRON_BUILDER_CACHE=Join-Path $scratchPath 'builder'
 Push-Location $root
 try{
  if($Mode -eq 'Install'){
   & npm.cmd ci --ignore-scripts --no-audit --no-fund
   if($LASTEXITCODE){throw "Pinned dependency installation failed: $LASTEXITCODE"}
  }elseif($Mode -eq 'Validate'){
   if(!(Test-Path (Join-Path $root 'docs\source-ready.json'))){throw 'Whole-scope source readiness record required before validation'}
   & node.exe node_modules\electron\install.js
   if($LASTEXITCODE){throw 'Pinned Electron download failed'}
   # Resolve locked local entrypoints explicitly; an unqualified npm script compiler lookup failed on this host.
   $node=(Get-Command node.exe).Source
   if($FirstCheck -eq 'Typecheck'){
    & $node node_modules\typescript\bin\tsc --noEmit -p tsconfig.node.json
    if($LASTEXITCODE){throw 'Main-process typecheck failed'}
    & $node node_modules\typescript\bin\tsc --noEmit -p tsconfig.web.json
    if($LASTEXITCODE){throw 'Renderer typecheck failed'}
   }
   & $node node_modules\eslint\bin\eslint.js .
   if($LASTEXITCODE){throw 'Lint failed'}
   & $node node_modules\vitest\vitest.mjs run --maxWorkers=2
   if($LASTEXITCODE){throw 'Tests failed'}
   & $node node_modules\electron-vite\bin\electron-vite.js build
   if($LASTEXITCODE){throw 'Application build failed'}
  }else{
   if(!$PackageDirectory){$PackageDirectory=Join-Path $root 'dist'}
   $env:REFRAME_PACKAGE_DIR=[IO.Path]::GetFullPath($PackageDirectory)
   & node.exe node_modules\electron-builder\cli.js --win --x64 --dir --publish never --config.directories.output $env:REFRAME_PACKAGE_DIR
   if($LASTEXITCODE){throw 'Windows packaging failed'}
   & node.exe scripts\portable.mjs
   if($LASTEXITCODE){throw 'Portable packaging failed'}
   & node.exe maintenance\inspect-payload.mjs (Join-Path $env:REFRAME_PACKAGE_DIR 'win-unpacked')
   if($LASTEXITCODE){throw 'Packaged payload scope verification failed'}
  }
 }finally{Pop-Location}
}finally{foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}}
