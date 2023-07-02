# QoirLib
## C++ library
`QoirLib.h` is a copy of the original qoir.h, Copyright 2022 Nigel Tao, visit https://github.com/nigeltao/qoir for the original source.

The only modification to this file compared to the original is that "DLL_EXPORT" has been added to the qoir_encode and qoir_decode function declarations that is defined on Windows and Linux so that it's exporting these functions to a library.
The project files and main.cpp included here merely exist to compile the QoirLib.dll binary library file from it using Visual Studio 2022 (probably earlier versions will work as well), a makefile is available for Linux g++ builds.

## C# wrapper class
`QoirLibCs.h` is a copy of the original qoir.h, Copyright 2022 Nigel Tao, visit https://github.com/nigeltao/qoir for the original source.

In addition to the modification for the C++ library the QOIR_IMPLEMENTATION define has been removed and all code that was within that define. This is so that this class can serve as a pure header file template for the C# wrapper.

The actual C# wrapper class is in Qoir.cs in this folder.

## Installation and usage
### Using the wrapper class
In order to use the wrapper class the binary QoirLib library is needed. For Windows the necessary `QoirLib.dll` file and for Linux the `libQoirLib.so` file compiled on Ubuntu 20.04 (glibc 2.31) are directly provided in the [binaries] folder for your convenience. Simply copy them to the same folder where your xObsBeam plugin file is located, other folders within your system PATH should also work.

If you want to build it yourself just compile the QoirLib.h file using the Visual Studio 2022 project provided here for Windows, or run `make` on Linux.

## Creating the wrapper class (for developers)
The wrapper class in Qoir.cs was generated using ClangSharpPInvokeGenerator from [ClangSharp project](https://github.com/dotnet/ClangSharp) with the generate.ps1 script in this folder. This is a PowerShell script and therefore is for Windows only.

Qoir.cs is not a fully featured managed wrapper class, it is a very basic wrapper that only exposes the (unsafe) qoir_encode() and qoir_decode() function calls that are needed for this project. Be aware of this when you want to use this in your own project.

### Prerequisites
In order to run the script and recreate the wrapper class the following needs to be fulfilled:
- Windows
- have ClangSharp installed (`winget install LLVM.LLVM` in a command line window), version 16.0.1 or later

The generated script won't work stand-alone, it includes types defined in files in the `ClangSharpAttributes` folder of the xObsBeam project. If you want to use the wrapper class outside of this project also copy this folder.
