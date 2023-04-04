# xObsBeam
OBS plugin to transmit uncompressed video and audio feeds between OBS instances.

![image](https://user-images.githubusercontent.com/528974/229695123-33b165ba-019a-48ce-9197-3d203627352b.png)

## Prerequisites
- OBS 29+ 64 bit
- Currently only working on Windows (tested only on Windows 10, but Windows 11 should also work)

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
