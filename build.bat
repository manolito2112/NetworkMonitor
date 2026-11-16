@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Could not find .NET Framework C# compiler at %CSC%
    exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0NetworkMonitor.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.dll "%~dp0NetworkMonitor.cs"
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Built %~dp0NetworkMonitor.exe successfully.
