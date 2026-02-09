# SpeedSwitchHub

A Windows system tray application that automatically switches audio devices when a Logitech G Pro X 2 Lightspeed headset is powered on or off.

## Problem

The Logitech USB dongle stays active in Windows even when the headset is off or connected via 3.5mm cable. Windows keeps routing audio to the silent wireless channel, forcing you to manually switch devices. Worse, many games and apps (Cyberpunk 2077, Call of Duty, etc.) hold onto the original audio device and won't recognize a default device change mid-session.

## How it works

1. Polls the Logitech dongle over USB HID to detect the headset's actual power state
2. When the headset turns on/off, switches the Windows default audio device to the configured wireless or wired output
3. Uses undocumented Windows COM interfaces (`IPolicyConfig`) to force active audio sessions onto the new device -- no app restart needed
4. Optionally runs a low-latency audio proxy (Rust) for games that completely ignore device switching: games output to VB-Cable, the proxy captures it and forwards to the real speaker, and the proxy's output can be hot-swapped via IPC

## Architecture

```
src/
  GAutoSwitch.Core/        C# - Interfaces, models, services (business logic)
  GAutoSwitch.Hardware/     C# - HID communication, WASAPI interop, audio switching
  GAutoSwitch.UI/           C# - WinUI 3 system tray app (settings, tray icon)
  audio-proxy/              Rust - WASAPI capture/render audio forwarding
build/
  Build-Release.ps1         PowerShell - Builds everything and packages with Squirrel
```

## Prerequisites

- Windows 10 (build 17763+)
- .NET 10 SDK
- Rust toolchain (for the audio proxy)
- MSYS2 with MinGW (for GNU linker targets)
- [VB-Cable](https://vb-audio.com/Cable/) (only if using the audio proxy feature)

## Building

### Full release build

```powershell
.\build\Build-Release.ps1 -Version "1.0.0" -Architecture "x64"
```

This builds the Rust audio proxy, publishes the .NET app as self-contained, and packages everything into a Squirrel installer under `releases\x64\`.

Supported architectures: `x64`, `x86`, `arm64`.

### Development build

Open `GAutoSwitch.slnx` in Visual Studio or Rider and build normally. The audio proxy must be built separately:

```
cd src\audio-proxy
cargo build --release --target x86_64-pc-windows-gnu
```

The .NET post-build step copies `audio-proxy.exe` from the Rust target directory into the output.

## Configuration

Settings are stored as JSON in `%AppData%\GAutoSwitch`. The UI lets you configure:

- Wireless and wired playback devices
- Wireless and wired microphones
- Audio proxy toggle and buffer size (default 10ms, configurable down to 1ms)
- Microphone proxy toggle
- Launch on Windows startup
- Start minimized to tray

## Tech stack

| Component | Stack |
|---|---|
| Core logic | C# / .NET 10 |
| UI | WinUI 3 (Windows App SDK) |
| Hardware detection | HID++ protocol via hidlibrary |
| Audio switching | WASAPI, undocumented IPolicyConfig COM |
| Audio proxy | Rust (wasapi, ringbuf crates) |
| IPC | Windows named pipes (JSON messages) |
| Installer | Clowd.Squirrel |

## License

MIT
