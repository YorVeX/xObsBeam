@echo off

for %%I in (..\.) do set "ProjectName=%%~nI"

mkdir ..\release\win-x64\obs-plugins\64bit 2>nul
mkdir ..\release\win-x64\data\obs-plugins\%ProjectName%\locale 2>nul
del /F /S /Q ..\release\win-x64\obs-plugins\64bit\*
del /F /S /Q ..\release\win-x64\data\obs-plugins\%ProjectName%\locale\*
copy /Y ..\publish\win-x64\* ..\release\win-x64\obs-plugins\64bit\
copy /Y ..\libjpeg-turbo\binaries\win-x64\turbojpeg.dll ..\release\win-x64\obs-plugins\64bit\
copy /Y ..\QoirLib\binaries\win-x64\QoirLib.dll ..\release\win-x64\obs-plugins\64bit\
copy /Y ..\FpngeLib\binaries\win-x64\FpngeLib.dll ..\release\win-x64\obs-plugins\64bit\
copy /Y ..\Density\binaries\win-x64\density.dll ..\release\win-x64\obs-plugins\64bit\
copy /Y ..\locale\* ..\release\win-x64\data\obs-plugins\%ProjectName%\locale
