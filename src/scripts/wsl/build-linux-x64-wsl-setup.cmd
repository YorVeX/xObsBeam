@echo off
setlocal enabledelayedexpansion

set "WSL_DISTRO=Ubuntu-22.04"

echo This script will help to initially set up WSL to be able to compile Linux binaries of OBS plugins on Windows.
echo.
echo Checking if %WSL_DISTRO% is installed...

REM Check if the required WSL distro is available.
wsl -d %WSL_DISTRO% echo "ok" >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
  echo.
  echo Error: %WSL_DISTRO% is not installed.
  echo.
  echo This script requires %WSL_DISTRO% to compile Linux binaries that are compatible with older glibc versions.
  echo.
  echo To install it, open a PowerShell console with administrator privileges and execute:
  echo.
  echo   wsl --install %WSL_DISTRO%
  echo.
  echo After installation finishes, run `sudo apt update` and `sudo apt upgrade` once to bring all packages up to date.
  echo Then re-run this script.
  echo.
  pause
  exit /b 1
)
echo Found %WSL_DISTRO%
echo.

echo Please note: For easy accessibility this script will create a "/build-net" folder in the WSL root directory
echo (and not in the user home) that the default WSL user has full access to.
echo.
echo Press any key to proceed...
pause >nul

cls

echo Preparing build folder, installing .NET 10 SDK and necessary build depedencies...
REM All done in one line to prevent multiple sudo password prompts:
REM make is needed to rebuild the prebuilt native libraries (QoirLib, density) via rebuild-linux-libs.sh
wsl -d %WSL_DISTRO% sudo add-apt-repository -y ppa:dotnet/backports ^&^& sudo apt-get install -y dotnet-sdk-10.0 clang make zlib1g-dev ^&^& sudo mkdir -p /build-net ^&^& sudo chown -R $USER:$USER /build-net

if %ERRORLEVEL% == 0 (
  echo.
  echo All done, other WSL based build scripts can be used now.
) else (
  echo.
  echo Something went wrong, the script ran into error %ERRORLEVEL%
)

pause
