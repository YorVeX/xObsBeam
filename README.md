# xObsBeam
OBS plugin to transmit raw, uncompressed video and audio feeds between OBS instances. An alternative to NDI and Teleport for local data transmission.

![image](https://user-images.githubusercontent.com/528974/229695123-33b165ba-019a-48ce-9197-3d203627352b.png)

## Prerequisites
- OBS 29+ 64 bit
- Currently only working on Windows (tested only on Windows 10, but Windows 11 should also work)

## Use case
### Compression and resource usage
Both [NDI](https://github.com/obs-ndi/obs-ndi) and [Teleport](https://github.com/fzwoch/obs-teleport) do the same as this plugin, but transmit their video feeds using a lossy compression. They do this for a good reason, it saves a tremendous amount of bandwidth while at the same time to the human eye losing almost no visual quality (despite the term "lossy"). But while the visual quality shouldn't be of much concern, it also comes with a performance hit, since CPU and/or GPU resources are needed to do the compression work.

### Local transmission
A use case where this seems unnecessary is when trasmitting data from one OBS instance to another within the same machine (so no network between them). E.g. when the first OBS instance is for recording and it sends the feed to a second OBS instance for streaming, which adds some panels and effects only for the stream which shouldn't be visible in the recording (other solutions to achieve that currently don't seem to be really stable).

### Notes on performance
If you are hoping that this solution has zero resource usage you will be disappointed. Beside the obvious memory needs to store several uncompressed frames on both sender and receiver side (a small queue is always needed) a significant amount of CPU usage is already caused at the moment where you activate any output plugin in OBS, even if the plugin is not doing anything with that data at all. Also managing and transferring vast amounts of data takes some resources. However, it will still be less when encoding/compression would be added on top of this.

### Enthusiasts, edge cases and unteachables
While the main idea is to use this for local transmission this plugin technically also works when used over a network, so that enthusiasts with expensive network setups can play around with and might actually make it work for them. And at least transmitting sources with low resolution and/or low FPS through the network could actually be feasible transmitting even over a standard network, e.g. for a retro game or a cam feed of an old webcam that you want to include.
Also there are these unteachable people who keep on asking for uncompressed transmission despite being told multiple times that it's not a good idea, insisting that their great 1 Gbps connection will certainly be able to handle it. They're in for a disappointing experience, but at least now you can point them somewhere to try and see for themselves (be warned that for this reason GitHub issues using this plugin on network are likely to be closed without investigation).

## Usage
_TBD_

## FAQ
- **Q**: Why is the plugin file so big compared to other plugins for the little bit it does, will this cause issues?
  - **A**: Unlike other plugins it's not written directly in C++ but in C# using .NET 7 and NativeAOT (for more details read on in the section for developers). This produces some overhead in the actual plugin file, however, the code that matters for functionality of this plugin should be just as efficient and fast as code directly written in C++ so there's no reason to worry about performance on your system.

- **Q**: Will there be a version for other operating systems, e.g. Linux or MacOS?
  - **A**: The project can already be built on Linux and produce a plugin file, however, trying to load it makes OBS crash on startup and I don't know why, since I don't have any Linux debugging experience. If you think you can help this would be much appreciated, [please start here](https://github.com/YorVeX/ObsCSharpExample/issues/2).
  MacOS is even more complicated, since it [is currently only supported by the next preview version of .NET 8](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#platformarchitecture-restrictions), although people [do already successfully create builds](https://github.com/dotnet/runtime/issues/79253) with it. This will also need help from the community, I won't work on that myself.

- **Q**: Will there be a 32 bit version of this plugin?
  - **A**: No. Feel free to try and compile it for x86 targets yourself, last time I checked it wasn't fully supported in NativeAOT.


## For developers
### C#
OBS Classic still had a [CLR Host Plugin](https://obsproject.com/forum/resources/clr-host-plugin.21/), but with OBS Studio writing plugins in C# wasn't possible anymore. This has changed as of recently as you can see.

### Building
Refer to the [building instructions for my example plugin](https://github.com/YorVeX/ObsCSharpExample#building), they will also apply here.

## Credits
Many thanks to [kostya9](https://github.com/kostya9) for laying the groundwork of C# OBS Studio plugin creation, without him this plugin (and hopefully many more C# plugins following in the future) wouldn't exist. Read about his ventures into this area in his blog posts [here](https://sharovarskyi.com/blog/posts/dotnet-obs-plugin-with-nativeaot/) and [here](https://sharovarskyi.com/blog/posts/clangsharp-dotnet-interop-bindings/). 
