@echo off
setlocal
cd /d "%~dp0"

rem Uses the C# compiler that ships with Windows (.NET Framework 4.x) - nothing to install.
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find the .NET Framework C# compiler in %WINDIR%\Microsoft.NET.
    pause
    exit /b 1
)

echo Building AutoMute.exe ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:AutoMute.exe ^
    /win32icon:AutoMute.ico /win32manifest:app.manifest ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    src\*.cs
if errorlevel 1 (
    echo.
    echo BUILD FAILED - copy the error lines above and send them over.
    pause
    exit /b 1
)
echo.
echo Done: %~dp0AutoMute.exe
echo AutoMute.exe is self-contained - copy it anywhere and run it.
pause
