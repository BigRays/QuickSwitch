@echo off
rem Build QuickSwitch.exe using the .NET Framework compiler bundled with Windows.
rem If QuickSwitch.exe is running, exit it from the tray icon first.

setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [ERROR] .NET Framework compiler csc.exe not found.
    exit /b 1
)

set ICO=
if exist QuickSwitch.ico set ICO=/win32icon:QuickSwitch.ico

"%CSC%" /nologo /target:winexe /codepage:65001 /out:QuickSwitch.exe ^
    /r:Microsoft.CSharp.dll /r:System.dll /r:System.Core.dll ^
    /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    %ICO% QuickSwitch.cs

if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)
echo Build OK: QuickSwitch.exe
endlocal
