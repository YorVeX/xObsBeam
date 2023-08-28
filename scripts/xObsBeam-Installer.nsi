; if you want to build this, you need to download and install NSIS from https://nsis.sourceforge.io/Download or
; execute this on a CMD or PowerShell window: winget install NSIS.NSIS

Unicode True

!define AUTHOR "YorVeX"

; Automatically detect the name of the parent directory to use it as the app name
!tempfile MYINCFILE
!system 'for %I in (..\.) do echo !define APPNAME "%~nxI" > "${MYINCFILE}"'
!include "${MYINCFILE}"
!delfile "${MYINCFILE}"
!define APPDISPLAYNAME "${APPNAME} OBS Plugin"

; Automatically detect the version of the library in the publish folder to use it as the app version
!getdllversion "..\publish\win-x64\${APPNAME}.dll" ver
!define VERSION "${ver1}.${ver2}.${ver3}.${ver4}"
!define DISPLAY_VERSION "v${ver1}.${ver2}.${ver3}"
VIProductVersion "${VERSION}"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "VIProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2023 ${AUTHOR}, https://github.com/${AUTHOR}"
VIAddVersionKey "FileDescription" "${APPDISPLAYNAME}"

; Main install settings
Name "${APPDISPLAYNAME} ${DISPLAY_VERSION}"
Caption "Beam plugin for OBS Studio"
Icon "..\img\${APPNAME}-Icon.ico"
UninstallIcon "..\img\${APPNAME}-Icon.ico"
InstallDirRegKey HKLM "Software\${APPNAME}" ""
InstallDir "$PROGRAMFILES64\obs-studio"
OutFile "..\release\win-x64\${APPNAME}-win-x64-installer.exe"
SetCompressor LZMA

; Define splash screen
Function .onInit
	InitPluginsDir
	SetOutPath "$PLUGINSDIR"

	File "/oname=$PLUGINSDIR\Splash.jpg" "..\img\${APPNAME}-SplashScreen.jpg"
	; Need to download and install this plugin for this to work: https://nsis.sourceforge.io/NewAdvSplash_plug-in (the default plugins can only use BMP files)
	newadvsplash::show 1500 500 500 0x04025C /NOCANCEL "$PLUGINSDIR\Splash.jpg"
	Delete "$PLUGINSDIR\Splash.jpg"
FunctionEnd

; Modern interface settings
!include "MUI.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON "..\img\${APPNAME}-Icon.ico"
!define MUI_UNICON "..\img\${APPNAME}-Icon.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; Set languages (first is default language)
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "French"
!insertmacro MUI_LANGUAGE "German"
!insertmacro MUI_LANGUAGE "Spanish"
!insertmacro MUI_LANGUAGE "Italian"
!insertmacro MUI_LANGUAGE "Dutch"
!insertmacro MUI_LANGUAGE "PortugueseBR"
!insertmacro MUI_RESERVEFILE_LANGDLL

Var INSTALL_BASE_DIR
Var OBS_INSTALL_DIR

Section "${APPDISPLAYNAME}" Section1
	StrCpy $INSTALL_BASE_DIR "$PROGRAMFILES64\obs-studio"

	ReadRegStr $OBS_INSTALL_DIR HKLM "SOFTWARE\OBS Studio" ""

	!if "$OBS_INSTALL_DIR" != ""
		StrCpy $INSTALL_BASE_DIR "$OBS_INSTALL_DIR"
	!endif

	StrCpy $InstDir "$INSTALL_BASE_DIR"

	IfFileExists "$INSTDIR\*.*" +3
		MessageBox MB_OK|MB_ICONSTOP "OBS directory doesn't exist!"
		Abort

	!define UNINSTALLER "$INSTDIR\obs-plugins\uninstall-${APPNAME}-win-x64.exe"

	; Set Section properties
	SetOverwrite on
	AllowSkipFiles off

	; Set Section Files and Shortcuts
	SetOutPath "$INSTDIR\obs-plugins\64bit\"
	File /r "..\publish\win-x64\*.*" ; DLL and PDB file
	File "..\Density\binaries\win-x64\density.dll" ; Density library file
	File "..\QoirLib\binaries\win-x64\QoirLib.dll" ; QOIR library file
	File "..\libjpeg-turbo\binaries\win-x64\turbojpeg.dll" ; libjpeg-turbo library file

	SetOutPath "$INSTDIR\data\obs-plugins\${APPNAME}\locale\"
	File /r "..\locale\*.*" ; locale files

	CreateDirectory "$SMPROGRAMS\${APPDISPLAYNAME}"
	CreateShortCut "$SMPROGRAMS\${APPDISPLAYNAME}\Uninstall ${APPDISPLAYNAME}.lnk" "${UNINSTALLER}"

SectionEnd

Section -FinishSection

	WriteRegStr HKLM "Software\${APPNAME}" "InstallDir" "$INSTDIR"
	WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayName" "${APPDISPLAYNAME}"
	WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "UninstallString" "${UNINSTALLER}"
	WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "Publisher" "${AUTHOR}"
	WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "HelpLink" "https://github.com/${AUTHOR}/${APPNAME}"
	WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayVersion" "${DISPLAY_VERSION}"
	WriteUninstaller "${UNINSTALLER}"

SectionEnd

; Modern install component descriptions
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
	!insertmacro MUI_DESCRIPTION_TEXT ${Section1} "Install the ${APPDISPLAYNAME} to your installed OBS Studio version"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

UninstallText "This will uninstall the ${APPDISPLAYNAME} from your system"

;Uninstall section
Section Uninstall
	SectionIn RO
	AllowSkipFiles off

	;Remove from registry...
	DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
	DeleteRegKey HKLM "SOFTWARE\${APPNAME}"

	; Clean up the plugin files
	Delete /REBOOTOK "$INSTDIR\64bit\${APPNAME}.dll"
	Delete /REBOOTOK "$INSTDIR\64bit\${APPNAME}.pdb"
	; Clean up the third party library files
	Delete /REBOOTOK "$INSTDIR\64bit\density.dll"
	Delete /REBOOTOK "$INSTDIR\64bit\density.pdb"
	Delete /REBOOTOK "$INSTDIR\64bit\FpngeLib.dll"
	Delete /REBOOTOK "$INSTDIR\64bit\FpngeLib.pdb"
	Delete /REBOOTOK "$INSTDIR\64bit\QoirLib.dll"
	Delete /REBOOTOK "$INSTDIR\64bit\QoirLib.pdb"
	Delete /REBOOTOK "$INSTDIR\64bit\turbojpeg.dll"

	; Clean up the data (including locale) files
	RMDir /r /REBOOTOK "$INSTDIR\..\data\obs-plugins\${APPNAME}"

	; Delete self
	Delete "$INSTDIR\uninstall-${APPNAME}-win-x64.exe"

	; Delete Shortcuts
	RMDir /r "$SMPROGRAMS\${APPDISPLAYNAME}"

SectionEnd
