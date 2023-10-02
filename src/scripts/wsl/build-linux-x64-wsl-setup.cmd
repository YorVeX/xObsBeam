@echo off
echo This script will help to initially set up WSL to be able to compile Linux binaries of OBS plugins on Windows.
echo.
echo Before running this you first need to manually install WSL with Ubuntu-20.04 LTS.
echo This script assumes use of Ubuntu-20.04 LTS instead of the latest version so that produced binaries are also
echo compatible with older glibc versions. 
echo To install this open a PowerShell console with administrator privileges and execute this:
echo wsl --install --distribution Ubuntu-20.04
echo.
echo After installation was finished run `sudo apt update` and `sudo apt upgrade` once so that all packages are up to date.
echo Then exit this script, restart and only continue when the following lines list the WSL environment you want to use for building as default.
echo [96m
wsl -l
echo [0m
echo Press any key when the correct WSL distribution is shown as default and you are ready to continue...
pause >nul

cls

echo [101;93mPlease note:[0m For easy accessibility this script will create a "/build-net" folder in the WSL root directory
echo (and not in the user home) that the default WSL user has full access to.
echo.
echo Press any key to proceed...
pause >nul

cls

echo Preparing build folder, installing .NET 7 SDK and necessary build depedencies...
REM All done in one line to prevent multiple sudo password prompts:
wsl curl -sSL https://packages.microsoft.com/keys/microsoft.asc ^| sudo apt-key add - ^&^& sudo apt-add-repository https://packages.microsoft.com/ubuntu/20.04/prod ^&^& sudo apt-get install -y dotnet-sdk-7.0 clang zlib1g-dev ^&^& sudo mkdir -p /build-net ^&^& sudo chown -R $USER:$USER /build-net

if %ERRORLEVEL% == 0 (
  echo All done, other WSL based build scripts can be used now.
) else (
  echo Something went wrong, the script ran into error %ERRORLEVEL%
)



pause
