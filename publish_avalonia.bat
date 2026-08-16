@echo off
setlocal enabledelayedexpansion

rem Publishes ReplayCapture.App as a self-contained Native AOT binary.
rem
rem Native AOT's ilc step shells out to the MSVC linker (link.exe) via vswhere, and neither is
rem normally on PATH outside a "Developer Command Prompt" — this script finds them itself so the
rem publish works from a plain shell.

set "REPO_ROOT=%~dp0"
set "PROJECT=%REPO_ROOT%src\ReplayCapture.App\ReplayCapture.App.csproj"

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo ERROR: vswhere.exe not found at "%VSWHERE%".
    echo Install the "Desktop development with C++" workload ^(Visual Studio Build Tools or VS^) and retry.
    exit /b 1
)

for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do (
    set "VS_INSTALL_PATH=%%i"
)
if not defined VS_INSTALL_PATH (
    echo ERROR: vswhere could not find a Visual Studio/Build Tools install with the MSVC x64 toolset.
    echo Install the "Desktop development with C++" workload and retry.
    exit /b 1
)

set "VCVARS_VERSION_FILE=%VS_INSTALL_PATH%\VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt"
if not exist "%VCVARS_VERSION_FILE%" (
    echo ERROR: could not read the MSVC toolset version from "%VCVARS_VERSION_FILE%".
    exit /b 1
)
set /p VC_TOOLS_VERSION=<"%VCVARS_VERSION_FILE%"

set "MSVC_BIN=%VS_INSTALL_PATH%\VC\Tools\MSVC\%VC_TOOLS_VERSION%\bin\Hostx64\x64"
if not exist "%MSVC_BIN%\link.exe" (
    echo ERROR: link.exe not found at "%MSVC_BIN%".
    exit /b 1
)

set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%MSVC_BIN%"

echo Publishing ReplayCapture.App as Native AOT (win-x64, Release)...
dotnet publish "%PROJECT%" -r win-x64 -c Release -p:PublishAot=true -p:SelfContained=true
if errorlevel 1 (
    echo Publish failed.
    exit /b 1
)

echo.
echo Published to: src\ReplayCapture.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\
endlocal
