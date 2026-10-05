@echo off
title P3R Co-op - host (dev PC)
cd /d "%~dp0"
echo Which save slot do you play co-op on? (its number in the game's Load menu, e.g. 4)
set /p SLOT=Slot: 
start "P3R Co-op link - host" "%~dp0src\P3RCoopLink\bin\Release\net8.0\P3RCoopLink.exe" host --save %SLOT%
start "" "%USERPROFILE%\Reloaded-II\Reloaded-II.exe" --launch "D:\SteamLibrary\steamapps\common\P3R\P3R\Binaries\Win64\P3R.exe"
