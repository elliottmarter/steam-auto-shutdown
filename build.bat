@echo off
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:exe /out:Tests.exe src\Tracker.cs src\Tests.cs || exit /b 1
.\Tests.exe || exit /b 1
"%CSC%" /nologo /target:winexe /out:SteamAutoShutdown.exe /win32icon:src\app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll src\Tracker.cs src\SteamControl.cs src\App.cs || exit /b 1
del Tests.exe
echo Built SteamAutoShutdown.exe
