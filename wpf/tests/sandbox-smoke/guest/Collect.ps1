param([string]$Tag = 'final')
# Collects host/shell evidence into the Work mapping.
$work = 'C:\Coordinator\Work'
$out = Join-Path $work "collect-$Tag"
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item 'C:\ShellTest\profile\ptyhost\host.log' $out -ErrorAction SilentlyContinue
Get-ChildItem 'C:\ShellTest\profile' -Recurse -Force -ErrorAction SilentlyContinue | Select-Object FullName, Length | Out-File (Join-Path $out 'profile-tree.txt')
Get-Process | Where-Object { $_.ProcessName -match 'reframe|cmd|conhost|OpenConsole|dotnet' } |
    Select-Object Id, ProcessName, SessionId, MainWindowHandle, MainWindowTitle | Format-Table -AutoSize | Out-File (Join-Path $out 'processes.txt') -Width 300
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 1, 2, 3 } -MaxEvents 30 -ErrorAction SilentlyContinue |
    Select-Object TimeCreated, ProviderName, Id, Message | Format-List | Out-File (Join-Path $out 'app-events.txt') -Width 300
"query session:" | Out-File (Join-Path $out 'sessions.txt')
& query.exe session 2>&1 | Out-File (Join-Path $out 'sessions.txt') -Append
"done $Tag"
