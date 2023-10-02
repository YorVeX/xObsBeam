# Density API
## C++ library
Density is already provided as a dynamic library, hence the [density](density) sub folder simply contains the source for this library so that the exact version that xObsBeam was tested with is preserved here.

## C# wrapper class
DensityApi.cs is the C# wrapper for the Density library.

It is not a fully featured managed wrapper class, it instead is a very basic wrapper that only exposes the relevant function calls from the library that are needed for this project. Be aware of this when you want to use this in your own project.

## Using the wrapper class
In order to use the wrapper class the binary Density library is needed. For Windows the necessary `density.dll` file and for Linux the `libdensity.so` file compiled on Ubuntu 20.04 (glibc 2.31) are directly provided in the [binaries](binaries) folder for your convenience. Simply copy them to the same folder where your xObsBeam plugin file is located, other folders within your system PATH should also work.

If you want to build it yourself just compile the files using the Visual Studio 2022 project provided here for Windows, or run `make build/libdensity.so` on Linux. Note that the Makefile has been modified compared to the original, you need to use the Makefile provided here (the changes are documented as comments in the makefile).

## Creating the wrapper class (for developers)
The wrapper class in DensityApi.cs was generated using ClangSharpPInvokeGenerator from [ClangSharp project](https://github.com/dotnet/ClangSharp) with the generate.ps1 script in this folder. This is a PowerShell script and therefore is for Windows only.

### Prerequisites
In order to run the script and recreate the wrapper class the following needs to be fulfilled:
- Windows
- have ClangSharp (`winget install LLVM.LLVM` in a command line window) and ClangSharpPInvokeGenerator (`dotnet tool install --global ClangSharpPInvokeGenerator` in a command line window) installed, version 15.X or later

The generated script won't work stand-alone, it includes types defined in files in the `ClangSharpAttributes` folder of the xObsBeam project. If you want to use the wrapper class outside of this project also copy this folder.
