# HexDrive360

HexDrive360 is a Windows toolkit for connecting to an Xbox 360 development console over XBDM and inspecting or editing its live memory. It includes memory scanning, game offsets, XEX inspection, and scripting tools.

## Requirements

- Windows 10 or later
- .NET 8 Windows Desktop Runtime
- An Xbox 360 with XBDM enabled, reachable on the same network (TCP port 730 by default)

Some plugin features require JRPC2 on the console. No game files are included; add only files you are authorized to use.

## Run

Download and extract `HexDrive360-v1.0.0-win-x64.zip`, then start `HexDump360.exe`. Connect using the console's IP address.

## Build from source

```powershell
dotnet publish .\MemoryEditor.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

The project targets .NET 8 Windows Forms. The release is framework-dependent, so install the .NET 8 Windows Desktop Runtime if it is not already present.
