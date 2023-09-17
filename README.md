# xObsBeam
⚠️ Seeking help with translations, please go [here](https://obsproject.com/forum/threads/xobsbeam-beta.165709/page-5#post-624502) if you want to help, thanks!

OBS plugin to transmit lossless (raw or [QOI](https://qoiformat.org)/[LZ4](https://github.com/lz4/lz4) compressed) video and audio feeds between OBS instances. An alternative to NDI and Teleport for A/V transmission.

![image](https://user-images.githubusercontent.com/528974/229695123-33b165ba-019a-48ce-9197-3d203627352b.png)

## Prerequisites
- OBS 29.1.X+ 64 bit
- Windows
  - tested only on Windows 10, but Windows 11 should also work
- Linux
  - occasionally tested, but not regularly
  - binary build created on Ubuntu 20.04 WSL environment, therefore linked against glibc 2.31

## Use cases

This plugin transmits a video and audio feed from one OBS instance to another. The video feed can be compressed using [QOI](https://qoiformat.org) and/or [LZ4](https://github.com/lz4/lz4) with minimal CPU usage or even transmitted raw with no added compression CPU usage at all but at the expense of extreme bandwidth needs. In all cases the transmission is completely [lossless](https://en.wikipedia.org/wiki/Lossless_compression). The most common scenario would be transmitting audio and video feeds from OBS on a gaming PC to OBS on a streaming PC.

Both [NDI](https://github.com/obs-ndi/obs-ndi) and [Teleport](https://github.com/fzwoch/obs-teleport) in comparison transmit their video feeds using a lossy compression (albeit almost visually lossless to be fair) and most of the time have lower bandwidth needs than this plugin.

No solution is better or worse in general, it's just different tradeoffs regarding CPU/GPU usage, bandwidth needs and quality. What works best in a given scenario depends (among other things) on the specific use case and available resources. It's also very hard to predict how it will perform for any given setup, so it's best to test it out and play with the settings.

### Raw local transmission
The obvious case where compression seems unnecessary is when transmitting data from one OBS instance to another within the same machine (so no network between them). E.g. when the first OBS instance is for recording and it sends the feed to a second OBS instance for streaming, which adds some panels, alerts, animations and other effects only for the stream which shouldn't be visible in the recording (other solutions to achieve that currently don't seem to be really stable). In that case both encoding and decoding work would also hit the same machine.

### Standard setups
Standard setups (i.e. typical 1 Gbps consumer network gear) will almost always want to use the QOI compression and maybe LZ4 compression at FAST level in addition. You get a truly lossless video feed with low CPU usage on the bright side, but be warned that QOI/LZ4 have some worst case scenarios where bandwidth usage could have significant spikes. Make sure to run tests with various scenarios and see whether your setup is up for the task.

### "Small" sources
Transmitting sources with low resolution and/or low FPS through the network could actually be feasible even over a standard 1 Gbps network, e.g. for a retro game or a cam feed of an old webcam that you want to include. Especially when also the source PC is "retro" it helps a lot to save on CPU sources by not having to compress the data or using the CPU friendly QOI/LZ4 compressions.

### High bandwidth network
For enthusiast or semi-professional setups with expensive network devices it could be feasible to transmit raw video feeds over a network.

Here is some example configurations and their necessary bandwidths for the video feed (audio usually is negligible), exact numbers might vary a bit between xObsBeam versions but it helps to get a general idea:

| Resolution | FPS | NV12/I420 bandwidth | I444/P010/I010 bandwidth | BGRA bandwidth |
| --- | --- | --- | --- | --- |
| 720p | 30 | 312 Mpbs | 632 Mpbs | 840 Mbps |
| 720p | 60 | 632 Mpbs | 1264 Mpbs | 1680 Mbps |
| 900p | 30 | 488 Mpbs | 984 Mpbs | 1312 Mbps |
| 900p | 60 | 984 Mpbs | 1976 Mpbs | 2632 Mbps |
| 1080p (FHD) | 30 | 704 Mpbs | 1416 Mpbs | 1896 Mbps |
| 1080p (FHD) | 60 | 1416 Mpbs | 2840 Mpbs | 3792 Mbps |
| 1440p (2K) | 30 | 1264 Mpbs | 2528 Mpbs | 3368 Mbps |
| 1440p (2K) | 60 | 2528 Mpbs | 5056 Mpbs | 6744 Mbps |
| 2160p (4K) | 30 | 2840 Mpbs | 5688 Mpbs | 7592 Mbps |
| 2160p (4K) | 60 | 5688 Mpbs | 11384 Mpbs | 15184 Mpbs |

Remember that NV12 is the OBS default. If you choose a different color format also the load on the sender will increase in addition to the bandwidth demand.

If you want to get this number for your specific configuration just start the Beam output without compression and check the OBS log, it will show a line like this:
`[xObsBeam] Video output feed initialized, theoretical net bandwidth demand is 632 Mpbs`

Note that this is the theoretical **minimum net bandwidth** that is needed on a network. To measure your available net bandwidth you can use a tool like [iperf](https://iperf.fr), e.g. for a 2.5 Gbps connection it could be something like 2.37 Gbps.

In addition there still needs to be enough headroom available for spikes that can occur at any time and of course for all other traffic that wants to use the same interface. Depending on how sensitive this traffic is and how good all involved network devices are (switch, router, network card, cable, the chain is as good as its weakest link) other traffic might suffer long before you get even close to any theoretical limits, e.g. if you play a latency sensitive game on a cheap router your latency might already double our triple when only using half of theoretically available bandwidth or less because of how network packets are prioritized. Also other traffic originators might have spikes too.

If it's just that little bit of extra bandwidth that you lack to get it working, you can try to use only LZ4 at FAST level, it could e.g. bring an 800 Mbps feed down to 600 Mbps with very low CPU usage if you're lucky.


## Usage
Install the same version of the plugin (different versions are never guaranteed to be compatible to each other) into all OBS instances that you want to transmit video/audio feeds between.

### Installation
Before installing make sure that OBS is not running.

For portable mode simply extract the .7z file into the root directory of the portable folder structure. For regular OBS installations see the operating system specific instructions below.

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
One OBS instance will be the sender, on this instance go to the OBS main menu and select Tools -> Beam.

![image](https://user-images.githubusercontent.com/528974/229874785-8a504ebf-a743-4714-8acd-39651784d1c9.png)

A dialog will appear where you can configure the sender identifier and how the sender will accept receiver connections. Named pipe connection is the recommended connection type for local (within the same machine) connections, as it has the least overhead and therefore should come with the smallest resource impact. If you need to connect receiver and sender from different machines you need to use a TCP socket connection. Compression configuration depends on the setup and use case, read above sections for more information.
  
⚠️ During beta very many compression settings will be available to play around with, however, that doesn't mean they're all sensible choices. In fact, 80+% of combinations won't make sense, i.e. have high CPU usage but still also high bandwidth usage. After it has become a bit clearer what good combinations are others will be removed again when moving towards a stable release. In the meantime it's very important that you experiment and measure for yourself. If unsure, a safe option is using only LZ4 at FAST (level 1) for the lowest CPU usage and lowered but still very high bandwidth usage - it's also a compression mode that reliably works for color formats other than BGRA. If you're fine with setting the color format to BGRA (which comes with a CPU usage hit of its own but in some scenarios also with a quality boost depending on your inputs) then disable LZ4 and use QOI level 5, which is at already a bit less than half of raw bandwidth if you're lucky, or QOI level 10, which could get you down to less than a third of raw bandwidth usage on a sunny day while still being relatively nice to your CPU.

It doesn't seem reasonable to ever use higher LZ4 compression levels than FAST but since it was very easy to implement them I decided to put it in at least for the beta period, maybe someone finds a scenario where it's actually a good choice.

For the "Compress from OBS render thread" option the rule of thumb would be to leave it enabled as long as you're not dropping frames in any of the OBS instances, if you do, then disable that option (will be more likely to be necessary the higher the compression level you pick).

In any case I would be happy about reports of your experimentation results in the OBS forum.

![image](https://user-images.githubusercontent.com/528974/231322501-9bcd3efe-dde1-4d71-944e-7046f246fec4.png)

Check the "Enable Beam output" box if you want your output to be active now. Press OK to save the settings.

Note that as soon as the output is active your resource usage from OBS will go up, as OBS is now providing data to this plugin (regardless of whether the plugin is doing something with it at this point). Also certain video and audio settings (e.g. video resolution, FPS...) are locked in the OBS settings as long as an output is active. If you want to change those settings, you first need to disable the Beam output again.

An output is currently the only way to send data. A filter based sender solution to send only the data of a single source might be added to xObsBeam as a new feature later.

### Receiver configuration
At least one OBS instance will be the receiver (multiple receivers can connect to a sender), on a receiver instance add a new source of type Beam.

![image](https://user-images.githubusercontent.com/528974/229876072-47b20f3b-bac9-4b5d-ba99-8738aa43a14d.png)

Double click the source to edit its properties.

![image](https://user-images.githubusercontent.com/528974/231031395-081d0010-46a5-4c0c-b3b6-4141474237d0.png)

Make sure to select the same connection type that you previously selected for the sender, also enter the pipe name that you configured for the sender ("BeamSender" in this example). Press OK to save the settings.

Now you can show the new Beam source (click the eye icon next to it) and it should connect to the sender and start to receive data.


## FAQ
- **Q**: Why is the plugin file so big compared to other plugins for the little bit it does, will this cause issues?
  - **A**: Unlike other plugins it's not written directly in C++ but in C# using .NET 7 and NativeAOT (for more details read on in the section for developers). This produces some overhead in the actual plugin file, however, the code that matters for functionality of this plugin should be just as efficient and fast as code directly written in C++ so there's no reason to worry about performance on your system.

- **Q**: Will there be a version for MacOS?
  - **A**: NativeAOT [doesn't support cross-compiling](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/compiling.md#cross-architecture-compilation) and I don't have a Mac, so I currently can't compile it, let alone test it. You can try to compile it yourself, but note that MacOS [is currently only supported by the next preview version of .NET 8](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#platformarchitecture-restrictions), although people [do already successfully create builds](https://github.com/dotnet/runtime/issues/79253) with it.

- **Q**: Will there be a 32 bit version of this plugin?
  - **A**: No. Feel free to try and compile it for x86 targets yourself, last time I checked it wasn't fully supported in NativeAOT.

- **Q**: Does this work with all color formats and color spaces including HDR?
  - **A**: It should work fine when not using compression for every available OBS color format/space setting including HDR, since OBS data is just transferred 1:1. This is also true when using LZ4 compression. QOI compression, however, [is designed only for RGB(A)](https://qoiformat.org/qoi-specification.pdf). Its basic logic on byte level will still achieve some compression in most scenarios, but results may vary a lot, hence xObsBeam will show a warning when you enable QOI on any other color format than BGRA.


## For developers
### C#
OBS Classic still had a [CLR Host Plugin](https://obsproject.com/forum/resources/clr-host-plugin.21/), but with OBS Studio writing plugins in C# wasn't possible anymore. This has changed as of recently as you can see, this plugin is fully written in C#.

### Used libraries/technologies
This plugin uses the following libraries/technologies:
- [System.IO.Pipelines](https://docs.microsoft.com/en-us/dotnet/api/system.io.pipelines?view=net-7.0) PipeWriter/PipeReader for high-efficiency data transfer between sender and receiver
- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) for LZ4 compression
- [QOI](https://qoiformat.org)

Compression is simply applied to each frame separately, QOI is not a video codec anyway and LZ4 not even tailored for images. It just so happens that [QOI is compressible](https://github.com/phoboslab/qoi/issues/166) and LZ4 is a very fast compression algorithm, creating a good combination to further reduce bandwidth needs while still staying lossless and keeping CPU usage low.


### Building
Refer to the [building instructions for my example plugin](https://github.com/YorVeX/ObsCSharpExample#building), they will also apply here.

## Credits
Many thanks to [kostya9](https://github.com/kostya9) for laying the groundwork of C# OBS Studio plugin creation, without him this plugin (and hopefully many more C# plugins following in the future) wouldn't exist. Read about his ventures into this area in his blog posts [here](https://sharovarskyi.com/blog/posts/dotnet-obs-plugin-with-nativeaot/) and [here](https://sharovarskyi.com/blog/posts/clangsharp-dotnet-interop-bindings/). 

Also thanks to [fzwoch](https://github.com/fzwoch) for his wonderful [Teleport](https://github.com/fzwoch/obs-teleport) plugin and for many interesting discussions on Discord that helped me along the way of creating this and other plugins.
