# libjpeg-turbo wrapper
This is a C# wrapper class for the [libjpeg-turbo library](https://github.com/libjpeg-turbo/libjpeg-turbo).

This software is based in part on the work of the Independent JPEG Group.

It is also using the TurboJPEG API, for the license see [LICENSE](LICENSE).

## Installation and usage
### Using the wrapper class
<!-- ⚠️ The "JPEG Help" button in the Beam settings window links here (to this anchor) and needs to be changed in case this URL or heading name changes. -->
#### Windows
In order to use the wrapper class the binary libjpeg-turbo library is needed. For Windows the necessary `turbojpeg.dll` version 2.1.91 (3.0 beta 2) is directly provided here for your convenience. If you want to get it from the original source: it is included in the package [libjpeg-turbo-2.1.91-vc64.exe](https://sourceforge.net/projects/libjpeg-turbo/files/2.1.91%20%283.0%20beta2%29/libjpeg-turbo-2.1.91-vc64.exe).
This DLL should be placed in the same folder as the xObsBeam plugin for simplicity. Alternatively it can be placed in the `bin` folder of the OBS installation (where also obs64.exe is located), into Windows system directories where these libraries typically reside in, or into a directory that is in your PATH environment variable.

#### Linux
On Linux it is recommended to install one of the libjpeg-turbo packages provided either by your distribution or from [https://sourceforge.net/projects/libjpeg-turbo/files](https://sourceforge.net/projects/libjpeg-turbo/files). You need the `libturbojpeg.so.0` library file, e.g. on Ubuntu this would be contained in the [libturbojpeg](https://packages.ubuntu.com/search?keywords=libturbojpeg) package.

### Compatible versions and features
The wrapper class was more thoroughly tested in Windows with both libjpeg-turbo versions 2.1.5.1 and 2.1.91 (aka beta 3.0 beta 2) and had a quick and short test on Ubuntu 20.04 with the 2.0.3-0ubuntu1 version. In general it should also work with many older versions.

## Creating the wrapper class (for developers)
The wrapper class was generated using ClangSharpPInvokeGenerator from [ClangSharp project](https://github.com/dotnet/ClangSharp) with the generate.ps1 script in this folder. This is a PowerShell script and therefore is for Windows only.

### Prerequisites
In order to run the script and recreate the wrapper class the following needs to be fulfilled:
- Windows
- have ClangSharp installed (`winget install LLVM.LLVM` in a command line window), version 16.0.1 or later
- have Git for Windows installed (`winget install Git.Git` in a command line window)

The generated script won't work stand-alone, it includes types defined in files in the `ClangSharpAttributes` folder of the xObsBeam project. If you want to use the wrapper class outside of this project also copy this folder.

