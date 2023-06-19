# FpngeLib
## C++ library
fpnge.cc is a copy of the original fpnge.cc, Copyright 2021 Google LLC, visit https://github.com/veluca93/fpnge for the original source.

fpnge.h is a modified copy of the original fpnge.h, Copyright 2021 Google LLC, visit https://github.com/veluca93/fpnge for the original source.

The only modification to this file compared to the original is that "__declspec(dllexport)" has been added to the FPNGEEncode and FPNGEOutputAllocSize function declarations.
The project files and main.cpp included here merely exist to compile the FpngeLib.dll binary library file from it using Visual Studio 2022 (probably earlier versions will work as well).

## C# wrapper class
Fpnge.cs is the C# wrapper for the modified fpnge.h file.

It is not a fully featured managed wrapper class, it instead is a very basic wrapper that only exposes the (unsafe) FPNGEOutputAllocSize() and FPNGEEncode() function calls that are needed for this project. Be aware of this when you want to use this in your own project.

## Using the wrapper class
### Windows
In order to use the wrapper class the binary FpngeLib library is needed. For Windows the necessary `FpngeLib.dll` version is directly provided here for your convenience. 

This DLL should be placed in the same folder as the xObsBeam plugin for simplicity. Alternatively it can be placed in the `bin` folder of the OBS installation (where also obs64.exe is located), into Windows system directories where these libraries typically reside in, or into a directory that is in your PATH environment variable.

If you want to build it yourself just compile the FpngeLib project provided here using Visual Studio 2022.

### Linux
Since the original fpnge.cc and fpnge.h files can be used on Linux too creating a similar library build should be possible, but this hasn't been built or tested for Linux yet. Any PRs to add what is necessary for a Linux build are very welcome.

## Creating the wrapper class (for developers)
The wrapper class in Fpnge.cs was generated using ClangSharpPInvokeGenerator from [ClangSharp project](https://github.com/dotnet/ClangSharp) with the generate.ps1 script in this folder. This is a PowerShell script and therefore is for Windows only.

After generating the file the declaration for FPNGEOutputAllocSize() has to be manually changed to actually call the library function instead of doing the calculation locally, simply by copying the DllImport line and accessors from the FPNGEEncode method. This is necessary because 1) this function is used by xObsBeam to test whether this library is available and 2) it increases the chance that the wrapper stays compatible for future versions of the library, in the case where the function signatures stay the same but the code behind it including the allocation size calculation changes.

### Prerequisites
In order to run the script and recreate the wrapper class the following needs to be fulfilled:
- Windows
- have ClangSharp (`winget install LLVM.LLVM` in a command line window) and ClangSharpPInvokeGenerator (`dotnet tool install --global ClangSharpPInvokeGenerator` in a command line window) installed, version 15.X or later

The generated script won't work stand-alone, it includes types defined in files in the `ClangSharpAttributes` folder of the xObsBeam project. If you want to use the wrapper class outside of this project also copy this folder.
