@echo off
rem Starts the Reframe shell in the connected guest user's desktop session (sandbox.exec run_as ExistingLogin).
whoami > C:\Coordinator\Work\start-shell-user.txt
start "" C:\ShellTest\shell\Reframe.Shell.exe --profile C:\ShellTest\profile
