# libjpeg-turbo wrapper
This is a C# wrapper class for the [libjpeg-turbo library](https://github.com/libjpeg-turbo/libjpeg-turbo).

## Creating the wrapper class
It was generated using ClangSharpPInvokeGenerator from [ClangSharp project](https://github.com/dotnet/ClangSharp) with the generate.ps1 script. This is a PowerShell script and therefore is for Windows only.

## Prerequisites
In order to run the script and recreate the wrapper class the following needs to be fulfilled:
- Windows
- have ClangSharp installed (`winget install LLVM.LLVM` in a command line window), version 16.0.1 or later
- have Git for Windows installed (`winget install Git.Git` in a command line window)

The generated script includes types defined in files in the `ClangSharpAttributes` folder.

## Using the wrapper class
In order to use the wrapper class the binary libjpeg-turbo library is needed. Currently only Windows is supported. The necessary `turbojpeg.dll` is included in the package [https://sourceforge.net/projects/libjpeg-turbo/files/2.1.91%20%283.0%20beta2%29/libjpeg-turbo-2.1.91-vc64.exe/download](libjpeg-turbo-2.1.91-vc64.exe).
This DLL needs to be in the OBS bin folder, where also obs64.exe is located.
