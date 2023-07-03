# FpngeLib
## C++ library
fpnge.h is a modified copy of the original fpnge.h, Copyright 2021 Google LLC, visit https://github.com/veluca93/fpnge for the original source.

The modification to this file compared to the original is that "DLL_EXPORT" has been added to the FPNGEEncode and FPNGEOutputAllocSize function declarations that is defined on Windows and Linux so that it's exporting these functions to a library. In addition, the FPNGEOutputAllocSize function had its inline implementation replaced by a pure header declaration.

fpnge.cc is a modified copy of the original fpnge.cc, Copyright 2021 Google LLC, visit https://github.com/veluca93/fpnge for the original source.

The modification to this file from the original is that the FPNGEOutputAllocSize function code from fpnge.h has been moved here.

The project files and FpngeLib.cpp included here merely exist to compile the FpngeLib.dll binary library file from it using Visual Studio 2022 (probably earlier versions will work as well) on Windows or clang++ on Linux.

## C# wrapper class
Fpnge.cs is the C# wrapper for the modified fpnge.h file.

It is not a fully featured managed wrapper class, it instead is a very basic wrapper that only exposes the (unsafe) FPNGEOutputAllocSize() and FPNGEEncode() function calls that are needed for this project. Be aware of this when you want to use this in your own project.

## Using the wrapper class
In order to use the wrapper class the binary QoirLib library is needed. For Windows the necessary `FpngeLib.dll` file and for Linux the `libFpngeLib.so` file compiled on Ubuntu 20.04 (glibc 2.31) are directly provided in the [binaries](binaries) folder for your convenience. Simply copy them to the same folder where your xObsBeam plugin file is located, other folders within your system PATH should also work.

If you want to build it yourself just compile the files using the Visual Studio 2022 project provided here for Windows, or run `make CXX=clang++` on Linux (you could also try just `make`, but the original build script explicitly uses clang++ so that's what is recommended) to use the Makefile provided here.

## Creating the wrapper class (for developers)
The wrapper class in Fpnge.cs was generated using ClangSharpPInvokeGenerator from [ClangSharp project](https://github.com/dotnet/ClangSharp) with the generate.ps1 script in this folder. This is a PowerShell script and therefore is for Windows only.

After generating the file the declaration for FPNGEOutputAllocSize() has to be manually changed to actually call the library function instead of doing the calculation locally, simply by copying the DllImport line and accessors from the FPNGEEncode method. This is necessary because 1) this function is used by xObsBeam to test whether this library is available and 2) it increases the chance that the wrapper stays compatible for future versions of the library, in the case where the function signatures stay the same but the code behind it including the allocation size calculation changes.

### Prerequisites
In order to run the script and recreate the wrapper class the following needs to be fulfilled:
- Windows
- have ClangSharp (`winget install LLVM.LLVM` in a command line window) and ClangSharpPInvokeGenerator (`dotnet tool install --global ClangSharpPInvokeGenerator` in a command line window) installed, version 15.X or later

The generated script won't work stand-alone, it includes types defined in files in the `ClangSharpAttributes` folder of the xObsBeam project. If you want to use the wrapper class outside of this project also copy this folder.
