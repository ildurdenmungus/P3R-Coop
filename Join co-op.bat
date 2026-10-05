@echo off
title P3R Co-op - guest (dev PC)
cd /d "%~dp0"
echo Host IP address - or just press Enter to find the host on your LAN / VPN:
set /p IP=IP: 
echo Save slot for the host's save - Enter = first empty slot (an existing save there is backed up):
set /p SLOT=Slot: 
set SLOTARG=
if not "%SLOT%"=="" set SLOTARG=--slot %SLOT%
start "P3R Co-op link - guest" "%~dp0src\P3RCoopLink\bin\Release\net8.0\P3RCoopLink.exe" join %IP% %SLOTARG%
start "" "%USERPROFILE%\Reloaded-II\Reloaded-II.exe" --launch "D:\SteamLibrary\steamapps\common\P3R\P3R\Binaries\Win64\P3R.exe"
