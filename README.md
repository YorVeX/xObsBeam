# Beam
⚠️ Seeking help with translations, please go [here](https://obsproject.com/forum/threads/xobsbeam-beta.165709/page-5#post-624502) if you want to help, thanks!

Beam (technical project name "xObsBeam") is an OBS plugin to transmit video and audio feeds between OBS instances, raw, or with lossless or lossy compression. An alternative to NDI and Teleport for A/V transmission. Raw transmission and some compression options have alpha channel and HDR support.

![image](https://user-images.githubusercontent.com/528974/229695123-33b165ba-019a-48ce-9197-3d203627352b.png)

## Prerequisites
- OBS 29.1.X+ 64 bit
- Windows
  - tested only on Windows 10, but Windows 11 should also work
- Linux
  - occasionally tested, but not regularly
  - binary build created on Ubuntu 20.04 WSL environment, therefore linked against glibc 2.31

## Overview

This plugin transmits a video and audio feed from one OBS instance to another. The video feed can be compressed lossy or [lossless](https://en.wikipedia.org/wiki/Lossless_compression), or even transmitted raw with no added compression CPU usage at all but at the expense of extreme bandwidth needs.

The most common scenario would be transmitting audio and video feeds from OBS on a gaming PC to OBS on a streaming PC, another would be to run two OBS instances on the same PC, one for recording with a higher quality setting, and then transmit the video feed to a second OBS instance for streaming with lower quality, which adds some panels, alerts, animations and other effects only for the stream which shouldn't be visible in the recording (other solutions to achieve that currently don't seem to be really stable).

### Comparison
Both [NDI](https://github.com/obs-ndi/obs-ndi) and [Teleport](https://github.com/fzwoch/obs-teleport) can only transmit their video feeds using a lossy compression (albeit almost visually lossless to be fair). The JPEG compression that Teleport uses is also available in Beam, but with the additional option to only compress a certain percentage of frames, giving a fine grained control over the CPU usage vs. bandwidth usage tradeoff. Using this makes it possible to achieve both lower bandwidth and CPU usage than NDI, but this may depend on the content and other factors.

Beam also offers lossless compression options, including some that support alpha channels and HDR, which is not available in either NDI or Teleport. Also only Beam offers a frame buffer on the receiver that is able to compensate for network jitter and packet loss, which can be a big advantage in some scenarios, especially when trying to use this on unstable networks like Wifi (note that using Wifi for this kind of transmission is still not recommended and should be avoided if possible). Optionally a fixed delay between sender and receiver can be configured, which can be useful if outside audio or video sources need to be synced with the transmitted feed.

Beam basically aims to offer more options, flexibility and stability, however, in the end no solution is better or worse in general, it's just different tradeoffs regarding CPU/GPU usage, bandwidth needs, quality, ease of use and compatibility. What works best in a given scenario depends (among other things) on the specific use case and available resources. It's also very hard to predict how any solution will perform for any given setup, so it's best to test it out and play with the settings.

### Compression options
You want to pick raw transmission (simply don't select any compression) if you stay within the same computer (Named Pipe connection setting), because available bandwidth is not an issue in this case and you can save on CPU usage. It might also be feasible if you only transmit a feed with low resolution and/or low FPS or if you're on a 5G or 10G network.

If you're on a standard 1G network you will likely need to use compression to keep bandwidth needs in check. Try to stay lossless with QOY, and if that doesn't work you can always pick JPEG, which is still mostly visually lossless.

See [here](https://github.com/YorVeX/xObsBeam/wiki/Compression-help) for more details on the available compression options and raw bandwidth usage.

## Usage
Install the same version of the plugin (different versions are never guaranteed to be compatible to each other) into all OBS instances that you want to transmit video/audio feeds between.

### Installation
Before installing make sure that OBS is not running.

For regular OBS installations see the operating system specific instructions below. For portable mode simply extract the .7z file into the root directory of the portable folder structure.

<details>
<summary>🟦 Windows</summary>

For automatic installation just run the provided installer, then restart OBS.

For manual installation extract the downloaded .7z file (= copy the contained obs-plugins and data folders) into the OBS Studio installation directory. The default location for this is

`C:\Program Files\obs-studio`

This needs admin permissions.

</details>

<details>
<summary>🐧 Linux</summary>

The folder structure in the downloaded .7z file is prepared so that you can extract the file (= copy the contained files) into your user home and on many systems this will just work already.

However, depending on the distribution and OBS installation method (manual, distro repo, snap, flatpak...) the location of this folder can vary, so if it doesn't work from the user home you might have to look around a bit.

Example locations for the plugin .so (and .so.dbg) file are:

- `~/.config/obs-studio/plugins/` (The structure the .7z is prepared for)
- `/usr/lib/obs-plugins/`
- `/usr/lib/x86_64-linux-gnu/obs-plugins/`
- `/usr/share/obs/obs-plugins/`
- `~/.local/share/flatpak/app/com.obsproject.Studio/x86_64/stable/active/files/lib/obs-plugins/`
- `/var/lib/flatpak/app/com.obsproject.Studio/x86_64/stable/active/files/lib/obs-plugins/`

Unfortunately the expected location of the locale, which can be found in the data folder, can vary also.

If you get missing locale errors from the plugin you can try to copy the "locale" folder found inside the data folder to:

- `/usr/share/obs/obs-plugins/<plugin name>/locale`
- `~/.local/share/flatpak/app/com.obsproject.Studio/x86_64/stable/active/files/share/obs/obs-plugins/<plugin name>/locale`
- `/var/lib/flatpak/app/com.obsproject.Studio/x86_64/stable/active/files/share/obs/obs-plugins/<plugin name>/locale`

If in doubt, please check where other "en-US.ini" files are located on your system.

</details>

The steps to update an older version of the plugin to a newer version are the same, except that during file extraction you need to confirm overwriting existing files in addition.

### Sender configuration
One OBS instance will be the sender, on this instance go to the OBS main menu and select Tools -> Beam Sender Output.

![image](https://github.com/YorVeX/xObsBeam/assets/528974/a956f9be-76ac-4fb3-8bc3-6711998cd98e)

A dialog will appear where you can configure the sender identifier and how the sender will accept receiver connections. Named pipe connection is the recommended connection type for local (within the same machine) connections, as it has the least overhead and therefore should come with the smallest resource impact. If you need to connect receiver and sender from different machines you need to use a TCP socket connection. Compression configuration depends on the setup and use case, read above sections for more information.
  
For the "Compress from OBS render thread" option the rule of thumb would be to leave it enabled as long as you're not dropping frames in any of the OBS instances, if you do, then disable that option (will be more likely to be necessary the higher the compression level you pick).

![image](https://github.com/YorVeX/xObsBeam/assets/528974/0809c533-3f68-498c-ac7f-68ce7742cfcc)

Check the "Enable Beam Sender Output" box if you want your output to be active now. Press OK to save the settings.

Note that as soon as the output is active your resource usage from OBS will go up, as OBS is now providing data to this plugin (regardless of whether the plugin is doing something with it at this point). Also certain video and audio settings (e.g. video resolution, FPS...) are locked in the OBS settings as long as an output is active. If you want to change those settings, you first need to disable the Beam output again.

Another option for a sender would be a filter. For this right click an async source and choose Filters from the context menu:

![image](https://github.com/YorVeX/xObsBeam/assets/528974/94be1abe-6af2-4ba2-a361-87e25928235e)

Now in the "Audio/Video Filters" section click the + button and choose one of the Beam filters:

![image](https://github.com/YorVeX/xObsBeam/assets/528974/cf1588b5-146b-4686-aab2-c1645f65c166)

After creating the filter the sender options are the same as for the output.

Note that Beam filters are not available for sync sources, which include Game Capture or scenes, they don't have the "Audio/Video Filters" section. There is currently no plans to add this, you can [read more about the reasons here](https://obsproject.com/forum/threads/xobsbeam-beta.165709/post-624288).

### Receiver configuration
At least one OBS instance will be the receiver (multiple receivers can connect to a sender), on a receiver instance add a new source of type Beam Receiver.

![image](https://github.com/YorVeX/xObsBeam/assets/528974/742d8ec0-ec8d-44e4-bee5-472c92d465a8)

Double click the source to edit its properties.

![image](https://github.com/YorVeX/xObsBeam/assets/528974/7d14a149-4fd6-4910-9e85-d5523cc53666)

Make sure to select the same connection type that you previously selected for the sender, then you should be able to find the sender on the list of available Beam feeds (an output named "BeamSender" in this case). Press OK to save the settings.

Now you can show the new Beam source (click the eye icon next to it) and it should connect to the sender and start to receive data. Play with the other options as you wish, a render delay limit can make sure that your source is reconnected (resetting delay to a lower level again) if it ever becomes too high, a frame buffer can be used to compensate for network hiccups or other lags or to get a fixed delay between sender and receiver, by automatically increasing or decreasing the buffer to counter any changes in delay within OBS.

### Troubleshooting
See [the Wiki](https://github.com/YorVeX/xObsBeam/wiki/Troubleshooting) for troubleshooting help.

## FAQ
- **Q**: Why is the plugin file so big compared to other plugins for the little bit it does, will this cause issues?
  - **A**: Unlike other plugins it's not written directly in C++ but in C# using .NET 7 and NativeAOT (for more details read on in the section for developers). This produces some overhead in the actual plugin file, however, the code that matters for functionality of this plugin should be just as efficient and fast as code directly written in C++ so there's no reason to worry about performance on your system.

- **Q**: Will there be a version for MacOS?
  - **A**: NativeAOT [doesn't support cross-compiling](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/compiling.md#cross-architecture-compilation) and I don't have a Mac, so I currently can't compile it, let alone test it. You can try to compile it yourself, but note that MacOS [is currently only supported by the next preview version of .NET 8](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#platformarchitecture-restrictions), although people [do already successfully create builds](https://github.com/dotnet/runtime/issues/79253) with it.

- **Q**: Will there be a 32 bit version of this plugin?
  - **A**: No. Feel free to try and compile it for x86 targets yourself, last time I checked it wasn't fully supported in NativeAOT.

- **Q**: Does this work with all color formats and color spaces including HDR?
  - **A**: It should work fine when not using compression for every available OBS color format/space setting including HDR, since OBS data is just transferred 1:1. This is also true when using LZ4 or Density compression. QOI and QOIR compression, however, support alpha but [are designed only for RGB(A)](https://qoiformat.org/qoi-specification.pdf), and QOY is designed for YUV formats like the NV12 OBS default or I420, it doesn't support alpha either. The current JPEG implementation in Beam also doesn't support HDR formats or alpha channels, even if the JPEG spec in general would offer this.


## For developers
### C#
OBS Classic still had a [CLR Host Plugin](https://obsproject.com/forum/resources/clr-host-plugin.21/), but with OBS Studio writing plugins in C# wasn't possible anymore. This has changed with the release of .NET 7 and NativeAOT, and this plugin is fully written in C#.

### Used libraries/technologies
This plugin uses the following libraries/technologies:
- [NetObsBindings](https://github.com/kostya9/NetObsBindings) as a base for building OBS plugins in C#
- [System.IO.Pipelines](https://docs.microsoft.com/en-us/dotnet/api/system.io.pipelines?view=net-7.0) PipeWriter/PipeReader for high-efficiency data transfer between sender and receiver
- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) for LZ4 compression
- [QOI](https://qoiformat.org) - ported 1:1 to C#, also some comments were added, see [here](https://github.com/YorVeX/xObsBeam/blob/main/Qoi.cs) for the Beam implementation
- [QOIR](https://github.com/nigeltao/qoir) - optionally loaded from an external dynamic library, more details on the Beam implementation of it [here](https://github.com/YorVeX/xObsBeam/tree/main/QoirLib)
- [Density](https://github.com/k0dai/density) - optionally loaded from an external dynamic library, more details on the Beam implementation of it [here](https://github.com/YorVeX/xObsBeam/tree/main/Density)
- a modified version of [QOY](https://github.com/Chainfire/qoy) - see [source code comments](https://github.com/YorVeX/xObsBeam/blob/main/Qoy.cs) for the differences to the original

Compression is simply applied to each frame separately, QOI, QOIR, QOY and JPEG are image codecs and not video codecs anyway and LZ4 and Density not even tailored for images.

### Building
Refer to the [building instructions for my example plugin](https://github.com/YorVeX/ObsCSharpExample#building), they will also apply here.

## Credits
Many thanks to [kostya9](https://github.com/kostya9) for laying the groundwork of C# OBS Studio plugin creation, without him this plugin (and hopefully many more C# plugins following in the future) wouldn't exist. Read about his ventures into this area in his blog posts [here](https://sharovarskyi.com/blog/posts/dotnet-obs-plugin-with-nativeaot/) and [here](https://sharovarskyi.com/blog/posts/clangsharp-dotnet-interop-bindings/). 

Also thanks to [fzwoch](https://github.com/fzwoch) for his wonderful [Teleport](https://github.com/fzwoch/obs-teleport) plugin and for many interesting discussions on Discord that helped me along the way of creating this and other plugins.
