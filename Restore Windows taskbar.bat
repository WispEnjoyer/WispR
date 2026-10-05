@echo off
rem Brings the normal Windows taskbar back if WispR was closed forcefully.
taskkill /im WispR.exe /f >nul 2>&1
"%~dp0WispR.exe" --restore-taskbar
