$WSL_DISTRO = "Ubuntu-22.04"
$BuildFolderFromLinux = "/build-net"
$BuildFolderFromWindows = "\\wsl.localhost\$WSL_DISTRO\build-net"

# --------------------------------

# Check if the required WSL distro is available.
wsl -d $WSL_DISTRO echo "ok" 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
	Write-Host "`n$([char]0x1b)[91mError: $WSL_DISTRO is not installed.$([char]0x1b)[0m`n" -ForegroundColor Red
	Write-Host "Please install it first by running the setup script (build-linux-x64-wsl-setup.cmd).`n"
	pause
	exit 1
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ((Split-Path -Leaf $scriptDir) -ne "wsl") {
	Write-Host "Error: You need to run this script from the wsl sub-folder you found this in, aborting."
	pause
	exit 1
}

# Get the project name from the parent of the parent of the script folder.
$projectPath = Resolve-Path "$scriptDir\..\.."
$ProjectName = Split-Path -Leaf $projectPath

# Prepare the build folder for this project
wsl -d $WSL_DISTRO mkdir -p "$BuildFolderFromLinux/$ProjectName"

# Make sure the base build folder is empty, purging any previous data
wsl -d $WSL_DISTRO rm -Rf "$BuildFolderFromLinux/$ProjectName/*"

# Copy all necessary code files into the WSL build folder.
$sourcePath = Resolve-Path "$scriptDir\..\.."
robocopy.exe $sourcePath "$BuildFolderFromWindows\$ProjectName" *.cs *.csproj
robocopy.exe "$sourcePath\ClangSharpAttributes" "$BuildFolderFromWindows\$ProjectName\ClangSharpAttributes" *.cs *.csproj

# Run the build.
wsl -d $WSL_DISTRO dotnet publish "$BuildFolderFromLinux/$ProjectName" -c Release -o "$BuildFolderFromLinux/$ProjectName/publish" -r linux-x64 /p:NativeLib=Shared /p:SelfContained=true
if ($LASTEXITCODE -ne 0) {
	Write-Host "`nBuild failed with exit code $LASTEXITCODE" -ForegroundColor Red
	pause
	exit $LASTEXITCODE
}

# Copy the relevant build files back.
$publishDir = Join-Path $scriptDir "..\..\publish\linux-x64"
$publishDir = (Resolve-Path $publishDir -ErrorAction SilentlyContinue).Path
if (-not $publishDir) {
	$publishDir = New-Item -ItemType Directory -Force -Path (Join-Path $scriptDir "..\..\publish\linux-x64")
}
Copy-Item -Path "$BuildFolderFromWindows\$ProjectName\publish\*" -Destination $publishDir -Force

# Create release structure.
$releaseBase = Join-Path $scriptDir "..\..\release\linux-x64-glibc-2.31\.config\obs-studio\plugins\$ProjectName"
$releaseBinDir = Join-Path $releaseBase "bin\64bit"
$releaseLocaleDir = Join-Path $releaseBase "data\locale"
New-Item -ItemType Directory -Force -Path $releaseBinDir | Out-Null
New-Item -ItemType Directory -Force -Path $releaseLocaleDir | Out-Null

Remove-Item -Path "$releaseBinDir\*" -Force -Recurse -ErrorAction SilentlyContinue
Remove-Item -Path "$releaseLocaleDir\*" -Force -Recurse -ErrorAction SilentlyContinue

Copy-Item -Path "$publishDir\*" -Destination $releaseBinDir -Force
$localeSource = Join-Path $scriptDir "..\..\locale"
Copy-Item -Path "$localeSource\*" -Destination $releaseLocaleDir -Force

# Copy extra binaries to the release structure
$libBase = Join-Path $scriptDir "..\..\..\lib"
Copy-Item -Path "$libBase\QoirLib\binaries\linux-x64-glibc-2.31\libQoirLib.so" -Destination $releaseBinDir -Force
Copy-Item -Path "$libBase\Density\binaries\linux-x64-glibc-2.31\libdensity.so" -Destination $releaseBinDir -Force

# Final cleanup in WSL
wsl -d $WSL_DISTRO rm -Rf "$BuildFolderFromLinux/$ProjectName/*"