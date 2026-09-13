<div align="center">

# RGDSCapture for Linux

**A software capture card for the Anbernic RG Dual Screen**

Stream, record and monitor both screens of your RG DS on Linux — no hardware capture card required.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Linux](https://img.shields.io/badge/Platform-Linux-orange.svg)]()
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)]()
[![UI: Avalonia](https://img.shields.io/badge/UI-Avalonia%2011-9b59b6.svg)]()

</div>

---

## What This Is

The Linux port of [RGDSCapture](https://github.com/zyphusx/RGDSCapture). Same
app, same features; the interface is rebuilt on Avalonia because WPF is
Windows-only, and the platform layer underneath it is native to Linux rather
than emulated.

```
RG DS                              Linux PC
──────                             ────────
Top Screen ──── RTP/UDP:5000 ────► Screen 1 Display
Bot Screen ──── RTP/UDP:5001 ────► Screen 2 Display
Audio Out  ──── 3.5mm Cable  ────► Line-In → PipeWire
```

The app connects to the device over SSH, starts H.264 pipelines on it, and
receives both screens as low-latency RTP. Audio comes over a physical cable
into your line input, which keeps the network path video-only and the video
stable.

---

## Installing

### Flatpak (recommended)

Nobara ships Flatpak enabled, so this is the path of least resistance.

```bash
git clone https://github.com/zyphusx/RGDSCapture-Linux.git
cd RGDSCapture-Linux
./packaging/flatpak/build.sh
flatpak run io.github.zyphusx.RGDSCapture
```

`build.sh --bundle` also writes a single-file `.flatpak` you can hand to
someone else.

> **One value needs filling in before the first build.** The manifest builds
> FFmpeg 9.0.1 from source and the `sha256` for that tarball is a placeholder
> — the machine this port was written on could not reach `ffmpeg.org`, and a
> guessed checksum is worse than an obvious gap. Get the real one:
>
> ```bash
> curl -O https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz
> sha256sum ffmpeg-9.0.1.tar.xz
> ```
>
> then paste it into `packaging/flatpak/io.github.zyphusx.RGDSCapture.yml`.

### Running from source

```bash
dotnet run                      # needs .NET 10; uses the system FFmpeg 8.x
```

---

## Requirements

| | |
|---|---|
| **OS** | Any modern Linux desktop; developed against Nobara / Fedora |
| **Runtime** | .NET 10 (bundled in the Flatpak) |
| **FFmpeg** | 8.x — what Nobara ships, so nothing extra to install |
| **Audio** | PipeWire or PulseAudio |
| **Keyring** | Any Secret Service provider, for "remember credentials" |
| **Display** | Wayland or X11 |

### Why FFmpeg 8 specifically

The bindings resolve native libraries by soname — `libavcodec.so.62`,
`libavutil.so.60`, `libswscale.so.9`, `libswresample.so.6` — so a different
FFmpeg generation does not load at all rather than mis-binding. `8.x` is what
Nobara ships (8.1.2 at time of writing), so running from source needs nothing
installed beyond what is already there.

The Windows build tracks FFmpeg 9. Matching the distro here is deliberate:
security fixes then arrive through `dnf` and are RPM Fusion's job, rather than
being pinned to whenever this project last rebuilt. Distributions backport
fixes to the branch they ship, so a lower major is not a lower patch level —
`rpm -q --changelog ffmpeg-libs | grep CVE` shows what has been applied.

Inside the Flatpak that argument does not hold — a bundle is only as current
as its last rebuild — which is the main reason to prefer running from source
on Nobara.

To move to another generation, change the `FFmpeg.AutoGen` package version and
the matching `AvcodecMajor` in `Services/FFmpegLoader.cs`; they are generated
as a pair and must move together. `7.1.1` binds `so.61`, `8.1.0` binds
`so.62`, `9.0.1.1` binds `so.63`.

---

## What Changed From the Windows Build

The view-models and the capture pipeline are the same code. What differs is
everything that touched a Windows API:

| Area | Windows | Linux |
|---|---|---|
| UI framework | WPF | Avalonia 11 |
| Audio | NAudio (WinMM) | PulseAudio / PipeWire via `libpulse` |
| Saved password | DPAPI blob in `settings.json` | Desktop keyring (Secret Service) |
| Recording pipes | Windows named pipes | FIFOs |
| Paths | `%APPDATA%`, `MyVideos` | XDG base directories, `xdg-user-dirs` |
| FFmpeg | ~150 MB of DLLs shipped alongside (9.x) | System 8.x, or Flatpak-bundled |
| Window chrome | `WindowChrome` + `WM_GETMINMAXINFO` | Extended client area |
| Monitor geometry | `MonitorFromWindow` | Avalonia `Screens` |

A few of these are improvements rather than translations. The password is now
encrypted at rest by the keyring and unreadable while it is locked, where
DPAPI only ever protected it from other users on the same machine. The accent
glow is a border shadow instead of a render effect, so it no longer forces an
offscreen surface for every card it lands on. And closing a FIFO is a clean
end-of-stream, where a Windows pipe discards whatever the reader has not
consumed — which is why the original had to drain explicitly before closing.

---

## Sandbox Permissions

The Flatpak asks for what it needs and nothing more:

| Permission | Why |
|---|---|
| `--share=network` | SSH to the device; RTP on UDP 5000/5001 |
| `--socket=pulseaudio` | Line-in monitoring and recording |
| `--talk-name=org.freedesktop.secrets` | Saved SSH password |
| `--filesystem=xdg-videos` | Recordings and replays |
| `--filesystem=xdg-pictures` | Screenshots |
| `--socket=wayland` / `--socket=fallback-x11` / `--device=dri` | Display |

No access to your home directory beyond those two media folders.

---

## Where Files Go

| | |
|---|---|
| Settings | `~/.config/RGDSCapture/settings.json` |
| Crash log | `~/.local/state/RGDSCapture/crash.log` |
| Recordings | `~/Videos/RGDSCapture/` |
| Screenshots | `~/Pictures/RGDSCapture/` |

Under Flatpak these live in the app's own `~/.var/app/io.github.zyphusx.RGDSCapture/`
tree, except recordings and screenshots, which go to your real media folders.

---

## Device Support

| Device | Screens | Capture path |
|---|---|---|
| RG Dual Screen | 2 | GStreamer + `mpph264enc` (hardware) |
| RG353V | 1 | `ffmpeg` fbdev + `libx264` (software) |

---

## License

MIT — see [LICENSE](LICENSE). Same as the upstream Windows project, so fixes
can move in either direction.
