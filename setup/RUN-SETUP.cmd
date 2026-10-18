@echo off
rem Runs the one-time MicFX admin setup (asks for UAC).
powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"%~dp0setup-admin.ps1\"','-Downloads','\"%~dp0downloads\"'"
type "%TEMP%\micfx-setup.log"
