# If ClangSharpPInvokeGenerator is missing, just run this from a CMD or PowerShell window:
# dotnet tool install --global ClangSharpPInvokeGenerator
# You might also need to run this:
# winget install LLVM.LLVM


$filePath = "Fpng.cs"

ClangSharpPInvokeGenerator `
	-c single-file preview-codegen generate-macro-bindings unix-types <# configuration for the generator, need unix-types even on Windows so that "unsigned long" becomes nuint and not uint #> `
	--remap vector=Vector `
	--exclude FPNGEFillOptions <# don't need this for using the library #>  `
	--exclude fpng_adler32 <# don't need this for using the library #>  `
	--exclude fpng_crc32 <# don't need this for using the library #>  `
	--exclude fpng_encode_image_to_file <# don't need this for using the library #>  `
	--exclude fpng_decode_file <# don't need this for using the library #>  `
	--exclude FPNG_ENCODE_SLOWER <# don't need this for using the library #>  `
	--exclude FPNG_FORCE_UNCOMPRESSED <# don't need this for using the library #>  `
	--with-using FPNG_CRC32_INIT=ClangSharp FPNG_CRC32_INIT=System.Numerics <# central namespace for the generic attributes that all ClangSharp generated classes need #>  `
	--with-using FPNG_CRC32_INIT=System.Numerics <# needed for SIMD Vector #>  `
  --with-class FPNG_DECODE_SUCCESS=FPNG_DECODE_RESULT <# group this anonymous enum #>  `
  --with-class FPNG_DECODE_NOT_FPNG=FPNG_DECODE_RESULT <# group this anonymous enum #>  `
	--file fpng.h <# file we want to generate bindings for #>  `
	--file fpnge.h <# file we want to generate bindings for #>  `
    -n FpngLib <# namespace of the bindings #> `
    --methodClassName Fpng <# class name where to put methods #> `
    --libraryPath FpngLib <# name of the DLL #> `
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
