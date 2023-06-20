# Density API
## C++ library
Density is already provided as a dynamic library, hence the [density](density) sub folder simply contains the source for this library so that the exact version that xObsBeam was tested with is preserved here.

## C# wrapper class
DensityApi.cs is the C# wrapper for the Density library.

It is not a fully featured managed wrapper class, it instead is a very basic wrapper that only exposes the relevant function calls from the library that are needed for this project. Be aware of this when you want to use this in your own project.

## Using the wrapper class
### Windows
In order to use the wrapper class the binary Density library is needed. For Windows the necessary `density.dll` version is directly provided here for your convenience. 

This DLL should be placed in the same folder as the xObsBeam plugin for simplicity. Alternatively it can be placed in the `bin` folder of the OBS installation (where also obs64.exe is located), into Windows system directories where these libraries typically reside in, or into a directory that is in your PATH environment variable.

If you want to build it yourself just compile the project provided here in the [msvc](density/msvc) sub folder using Visual Studio 2022 (other versions might also work).

### Linux
Please see [the original instructions](https://github.com/k0dai/density#build) on how to build the library in the [density](density) sub folder for Linux.

## Creating the wrapper class (for developers)
The wrapper class in DensityApi.cs was generated using ClangSharpPInvokeGenerator from [ClangSharp project](https://github.com/dotnet/ClangSharp) with the generate.ps1 script in this folder. This is a PowerShell script and therefore is for Windows only.

### Prerequisites
In order to run the script and recreate the wrapper class the following needs to be fulfilled:
- Windows
- have ClangSharp (`winget install LLVM.LLVM` in a command line window) and ClangSharpPInvokeGenerator (`dotnet tool install --global ClangSharpPInvokeGenerator` in a command line window) installed, version 15.X or later

The generated script won't work stand-alone, it includes types defined in files in the `ClangSharpAttributes` folder of the xObsBeam project. If you want to use the wrapper class outside of this project also copy this folder.
