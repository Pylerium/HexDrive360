# HexDrive360

HexDrive360 is a Windows toolkit for connecting to an Xbox 360 development console or RGH Kernel 17559 over XBDM and inspecting or editing its live memory. It includes memory scanning, game offsets, XEX inspection, and scripting tools.

## Requirements

- Windows 10 or later, 64-bit (x64)
- An Xbox 360 with XBDM enabled and reachable over the network (TCP port 730 by default)

The release is self-contained and does not require a separate .NET installation. Some plugin features require JRPC2 on the console. No game files are included; add only files you are authorized to use.

## Run

Download and run `HexDump360.exe`, or extract the release ZIP and run the executable inside it. The app may extract bundled runtime files on first launch.

## Build from source

```powershell
dotnet publish .\MemoryEditor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o .\publish
```
