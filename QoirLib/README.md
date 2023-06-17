# QoirLib
## C++ library
QoirLib.h is a copy of the original qoir.h, Copyright 2022 Nigel Tao, visit https://github.com/nigeltao/qoir for the original source.

The only modification to this file compared to the original is that "__declspec(dllexport)" has been added to the qoir_encode and qoir_decode function declarations.
The project files and main.cpp included here merely exist to compile the QoirLib.dll binary library file from it using Visual Studio 2022 (probably earlier versions will work as well).

## C# wrapper class
QoirLibCs.h is a copy of the original qoir.h, Copyright 2022 Nigel Tao, visit https://github.com/nigeltao/qoir for the original source.

The modification to this file compared to the original is that "__declspec(dllexport)" has been added to the qoir_encode and qoir_decode function declarations and that the QOIR_IMPLEMENTATION define has been removed and all code that was within that define. This is so that this class can serve as a pure header file template for the C# wrapper.

The actual C# wrapper class is in Qoir.cs in this folder.

## Installation and usage
### Using the wrapper class
#### Windows
In order to use the wrapper class the binary QoirLib library is needed. For Windows the necessary `QoirLib.dll` version is directly provided here for your convenience. 

This DLL should be placed in the same folder as the xObsBeam plugin for simplicity. Alternatively it can be placed in the `bin` folder of the OBS installation (where also obs64.exe is located), into Windows system directories where these libraries typically reside in, or into a directory that is in your PATH environment variable.

If you want to build it yourself just compile the QoirLib.h file using the Visual Studio 2022 project provided here.

#### Linux
Since the original qoir.h file can be used on Linux too creating a similar library build should be possible, but this hasn't been built or tested for Linux yet. Any PRs to add what is necessary for a Linux build are very welcome.

## Creating the wrapper class (for developers)
The wrapper class in Qoir.cs was generated using ClangSharpPInvokeGenerator from [ClangSharp project](https://github.com/dotnet/ClangSharp) with the generate.ps1 script in this folder. This is a PowerShell script and therefore is for Windows only.

Qoir.cs is not a fully featured managed wrapper class, it is a very basic wrapper that only exposes the (unsafe) qoir_encode() and qoir_decode() function calls that are needed for this project. Be aware of this when you want to use this in your own project.

### Prerequisites
In order to run the script and recreate the wrapper class the following needs to be fulfilled:
- Windows
- have ClangSharp installed (`winget install LLVM.LLVM` in a command line window), version 16.0.1 or later

The generated script won't work stand-alone, it includes types defined in files in the `ClangSharpAttributes` folder of the xObsBeam project. If you want to use the wrapper class outside of this project also copy this folder.
