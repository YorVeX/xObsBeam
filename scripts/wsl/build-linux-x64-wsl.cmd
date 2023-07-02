@echo off
REM The base build folder containing all .NET projects to build using the WSL environment from within the WSL environment
set BuildFolderFromLinux=/build-net
set BuildFolderFromWindows=\\wsl.localhost\Ubuntu-20.04\build-net

REM --------------------------------

for %%I in (.) do set "ParentFolder=%%~nI"

if not "%ParentFolder%" == "wsl" (
  echo Error: You need to run this script from the wsl sub-folder you found this in, aborting.
  pause >nul
  exit 1
)

REM Get the name of the project from the parent folders name, assuming that it was Git cloned and has the right name.
for %%I in (..\..\.) do set "ProjectName=%%~nI"

REM Prepare the build folder for this project
wsl mkdir -p %BuildFolderFromLinux%/%ProjectName%

REM Make sure the base build folder is empty, purging any previous data
wsl rm -Rf %BuildFolderFromLinux%/%ProjectName%/*

REM Copy all necessary code files into the WSL build folder.
robocopy.exe ..\..\. %BuildFolderFromWindows%\%ProjectName% *.cs *.csproj
robocopy.exe ..\..\ClangSharpAttributes %BuildFolderFromWindows%\%ProjectName%\ClangSharpAttributes *.cs *.csproj
robocopy.exe ..\..\libjpeg-turbo %BuildFolderFromWindows%\%ProjectName%\libjpeg-turbo *.cs
robocopy.exe ..\..\QoirLib %BuildFolderFromWindows%\%ProjectName%\QoirLib *.cs
robocopy.exe ..\..\FpngeLib %BuildFolderFromWindows%\%ProjectName%\FpngeLib *.cs
robocopy.exe ..\..\Density %BuildFolderFromWindows%\%ProjectName%\Density *.cs

REM Run the build.
wsl dotnet publish %BuildFolderFromLinux%/%ProjectName% -c Release -o %BuildFolderFromLinux%/%ProjectName%/publish -r linux-x64 /p:NativeLib=Shared /p:SelfContained=true

REM Copy the relevant build files back.
mkdir ..\..\publish\linux-x64\ 2>nul
copy /Y %BuildFolderFromWindows%\%ProjectName%\publish\* ..\..\publish\linux-x64\

REM Create release structure.
mkdir ..\..\release\linux-x64-glibc-2.31\.config\obs-studio\plugins\%ProjectName%\bin\64bit 2>nul
mkdir ..\..\release\linux-x64-glibc-2.31\.config\obs-studio\plugins\%ProjectName%\data\locale 2>nul
del /F /S /Q ..\..\release\linux-x64-glibc-2.31\.config\obs-studio\plugins\%ProjectName%\bin\64bit\*
del /F /S /Q ..\..\release\linux-x64-glibc-2.31\.config\obs-studio\plugins\%ProjectName%\data\locale\*
copy /Y ..\..\publish\linux-x64\ ..\..\release\linux-x64-glibc-2.31\.config\obs-studio\plugins\%ProjectName%\bin\64bit\
copy /Y ..\..\locale\* ..\..\release\linux-x64-glibc-2.31\.config\obs-studio\plugins\%ProjectName%\data\locale

REM Copy extra binaries to the release structure
copy /Y ..\..\QoirLib\binaries\linux-x64-glibc-2.31\libQoirLib.so ..\..\release\linux-x64-glibc-2.31\.config\obs-studio\plugins\%ProjectName%\bin\64bit\
copy /Y ..\..\Density\binaries\linux-x64-glibc-2.31\libdensity.so ..\..\release\linux-x64-glibc-2.31\.config\obs-studio\plugins\%ProjectName%\bin\64bit\

REM Final cleanup in WSL
wsl rm -Rf %BuildFolderFromLinux%/%ProjectName%/*
