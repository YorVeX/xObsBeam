## Library file for WebP support in xObsBeam

### Install libwebp.dll on Windows
- Copy libwebp.dll to the **\bin\64bit** directory under your OBS installation directory (**C:\Program Files\obs-studio** by default)

### Build libwebp.dll on Windows
If you want to build this file yourself follow these steps.

Note: this has only been tested with the Visual Studio versions mentioned here, however, it will most likely also work with other versions, e.g. VS 2019 or MSVC v142.

- Download [https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.2.1.tar.gz](libwebp-1.2.1.tar.gz) and extract it to a folder (currently only 1.2.1 is working, newer or older versions will fail)
- Install Visual Studio 2022
- In the Visual Studio Installer go to "Individual Components" and from the "Compilers, build tools, runtimes" section tick the boxes for
  - [X] MSVC v143 - VS 2022 C++ x64/x86 build tools (Latest)
  - [X] C++/CLI support for v143 build tools (Latest)
- After installation run the "x64 Native Tools Command Prompt for VS 2022" from the start menu
- In the command prompt enter
  - `cd /D "C:\The\Path\Where\You\Extracted\libwebp-1.2.1"`
  - `nmake /f Makefile.vc CFG=release-dynamic RTLIBCFG=static OBJDIR=output`
- C:\The\Path\Where\You\Extracted\libwebp-1.2.1\output\release-dynamic\x64\bin\ will now contain libwebp.dll

### Verify installation
Run OBS and check the log for a message like this:

`[xObsBeam] WebP: supported, libwebp version 1.2.1`

Now in Tools -> Beam you should see that WebP is available as an option.