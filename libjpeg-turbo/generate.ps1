# If ClangSharpPInvokeGenerator is missing, just run this from a CMD or PowerShell window:
# dotnet tool install --global ClangSharpPInvokeGenerator
# You might also need to run this:
# winget install LLVM.LLVM

$turboJpegVersion = "2.1.91" # Version 3 Beta 2
$filePath = "LibJpegTurbo.cs"
$sourceFolder = "libjpeg-turbo-$turboJpegVersion"

if (Test-Path -Path $sourceFolder) {
	Write-Host "Deleting folder: $sourceFolder"
	Remove-Item -Path $sourceFolder -Recurse -Force
}

git clone -b $turboJpegVersion https://github.com/libjpeg-turbo/libjpeg-turbo $sourceFolder

ClangSharpPInvokeGenerator `
	-c single-file preview-codegen generate-macro-bindings unix-types <# configuration for the generator, need unix-types even on Windows so that "unsigned long" becomes nuint and not uint #> `
	--remap tjregion=TJRegion  <# rename classes breaking naming convention and align with the Java API #>  `
	--remap tjtransform=TJTransform  <# rename classes breaking naming convention and align with the Java API #>  `
	--remap tjscalingfactor=TJScalingFactor  <# rename classes breaking naming convention and align with the Java API #>  `
	--with-using TJRegion=ClangSharp <# central namespace for the generic attributes that all ClangSharp generated classes need #>  `
	--file .\$sourceFolder\turbojpeg.h <# file we want to generate bindings for #>  `
    -n LibJpegTurbo <# namespace of the bindings #> `
    --methodClassName TurboJpeg <# class name where to put methods #> `
    --libraryPath turbojpeg <# name of the DLL #> `
    -o .\$filePath <# output #>


# The file is generated with Unix line endings (probably because of the unix-types parameter), let's correct this here and also use UTF-8 BOM so it's in line with other source files.

# Read the file content
$fileContent = Get-Content -Path $filePath -Raw

# Convert line endings to Windows format (CRLF)
$fileContent = $fileContent -replace "`r?`n", "`r`n"

# Convert encoding to UTF-8 with BOM
$encoding = New-Object System.Text.UTF8Encoding($true)
$fileContentBytes = $encoding.GetBytes($fileContent)
$byteOrderMark = [System.Text.Encoding]::UTF8.GetPreamble()
$fileContentBytesWithBOM = $byteOrderMark + $fileContentBytes

# Write the modified content back to the file
Set-Content -Path $filePath -Value $fileContentBytesWithBOM -Encoding Byte
