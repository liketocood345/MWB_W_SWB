@echo off
REM No lingering console: hand off to VBS and exit immediately.
wscript.exe //B "%~dp0LaunchSwb.vbs"
if errorlevel 1 wscript.exe //B "C:\Users\Public\LaunchSwb.vbs"
exit /b 0