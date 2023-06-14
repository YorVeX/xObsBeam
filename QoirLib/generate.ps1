# If ClangSharpPInvokeGenerator is missing, just run this from a CMD window:
# dotnet tool install --global ClangSharpPInvokeGenerator

$filePath = "Qoir.cs"

ClangSharpPInvokeGenerator `
	-c single-file preview-codegen generate-macro-bindings unix-types <# configuration for the generator, need unix-types even on Windows so that "unsigned long" becomes nuint and not uint #> `
	--exclude qoir_private_table_noise <# don't need this for using the library #>  `
	--exclude qoir_pixel_format__bytes_per_pixel <# don't need this for using the library #>  `
	--exclude qoir_pixel_buffer__is_zero <# don't need this for using the library #>  `
	--exclude qoir_make_rectangle <# don't need this for using the library #>  `
	--exclude qoir_rectangle__intersect <# don't need this for using the library #>  `
	--exclude qoir_rectangle__is_empty <# don't need this for using the library #>  `
	--exclude qoir_rectangle__width <# don't need this for using the library #>  `
	--exclude qoir_rectangle__height <# don't need this for using the library #>  `
	--exclude qoir_calculate_number_of_tiles_1d <# don't need this for using the library #>  `
	--exclude qoir_calculate_number_of_tiles_2d <# don't need this for using the library #>  `
	--exclude qoir_lz4_block_decode <# don't need this for using the library #>  `
	--exclude qoir_lz4_block_encode_worst_case_dst_len <# don't need this for using the library #>  `
	--exclude qoir_lz4_block_encode <# don't need this for using the library #>  `
	--exclude qoir_decode_pixel_configuration <# don't need this for using the library #>  `
	--with-using qoir_pixel_configuration_struct=ClangSharp <# central namespace for the generic attributes that all ClangSharp generated classes need #>  `
	--file QoirLibCs.h <# file we want to generate bindings for #>  `
    -n QoirLib <# namespace of the bindings #> `
    --methodClassName Qoir <# class name where to put methods #> `
    --libraryPath QoirLib <# name of the DLL #> `
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
