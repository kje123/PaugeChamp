# PaugeChamp

A modern replacement for the Hauppauge HD PVR (model 1212, USB `2040:4900–4903/4982`) driver and capture software on Windows 11.

| Old (2012) | PaugeChamp |
|---|---|
| `hcwhdpvr.sys` kernel driver (AVStream/KS, built for Vista) | Microsoft's built-in **WinUSB** driver, signed and maintained by Microsoft |
| Hauppauge Capture / WinTV to change settings | **PaugeChamp**: a small app with live preview, settings, MP4/TS recording and a tray icon |
| DirectShow crossbar in OBS | **OBS script** with all settings inside OBS, plus a one-click Media Source |

## Why not a new kernel driver?

Windows 11 only loads kernel drivers that Microsoft has signed. That needs an EV code-signing certificate and a Partner Center account, and unsigned drivers only load with test-signing turned on. The HD PVR doesn't need anything a kernel driver offers anyway: it's a USB device that already outputs H.264/AAC in MPEG-TS. So the "driver" here is WinUSB, which ships with Windows, and all device logic runs in user mode (`src/HdPvrDevice.cs`). This is Microsoft's recommended approach for this kind of device. It also means a bug can't blue-screen your PC.

The USB protocol (authorization handshake, input/bitrate/picture commands, stream start/stop) follows the Linux kernel's `hdpvr` driver, the best public reference for this device. The HD PVR boots from its own flash, so no firmware file is needed.

## Setup

### 1. Switch the HD PVR to WinUSB (once)

**Option A: no downloads**
1. Open **Device Manager**, then **Sound, video and game controllers**, then **Hauppauge HD PVR Capture Device**.
2. Right-click it and choose **Update driver**, then **Browse my computer**, then **Let me pick from a list**.
3. Untick **Show compatible hardware**. Choose **Universal Serial Bus devices**, then **WinUsb Device**, then **Next**, and accept the warning.
4. Start PaugeChamp and click **Fix driver (admin)**. This registers a device interface so apps can open WinUSB, then restarts the device.

**Option B: [Zadig](https://zadig.akeo.ie)**
Go to Options > List All Devices, pick the HD PVR, choose **WinUSB** and click **Replace Driver**. No fix-up step is needed.

**To go back to Hauppauge:** in Device Manager, choose Update driver, then Let me pick, then **Hauppauge HD PVR Capture Device**. The old driver stays in the Windows driver store.

### 2. Run PaugeChamp

Windows **Smart App Control** blocks any unsigned `.exe` that Microsoft has no reputation data for, which includes anything built locally. You have two ways to run the app:

- **`bin\PaugeChamp.exe`**: build it with `build.ps1`. Works when Smart App Control allows it.
- **`PaugeChamp.ps1`**: runs the same code through PowerShell, which works even with Smart App Control on. Run `install-shortcut.ps1` once (right-click it and choose *Run with PowerShell*) to add **PaugeChamp** to the Start menu. It compiles `src\*.cs` in memory when it launches (about 2 seconds). Add `-Startup` to also start it in the tray when you sign in.

The app has to stay running (in the tray) for OBS to receive video. Closing the window only hides it; to quit, right-click the tray icon and choose **Exit**.

### 3. OBS

1. In OBS, go to **Tools**, then **Scripts**, click **+**, and pick `obs\paugechamp_obs.lua`.
2. Click **Add HD PVR source to current scene**. This creates a Media Source named "HD PVR 1212" that reads `udp://127.0.0.1:5004`.
3. Change input, audio, bitrate, picture and recording settings in that panel. They apply to the device right away.

To add the source by hand instead: create a Media Source, untick *Local file*, set Input to `udp://127.0.0.1:5004?overrun_nonfatal=1&fifo_size=1000000` and Input format to `mpegts`.

## Settings

| Setting | Values | Applies |
|---|---|---|
| Video input | Component, S-Video, Composite | restarts stream (~5 s, firmware limit) |
| Audio input | RCA rear, RCA front, Optical S/PDIF | restarts stream |
| Audio format | AAC, AC-3 (firmware 0x0D+) | restarts stream |
| Analog standard | NTSC 60 Hz, PAL 50 Hz (S-Video/composite) | restarts stream |
| Bitrate mode | CBR, VBR, VBR peak-limited | live |
| Average / peak bitrate | 1–13.5 / 1.1–20.2 Mbps | live |
| Brightness, contrast, hue, saturation, sharpness | 0–255 (hue 0–30 on newer firmware) | live |
| Boost analog audio | on/off | live |

The HD PVR doesn't scale video. It encodes whatever resolution comes in (up to 1080i), and the app shows the detected input signal.

## Preview

The left side of the window shows a live preview of what the HD PVR is capturing. It's decoded with Windows' built-in H.264 decoder and shown at the source frame rate (up to 60 fps), paced by the stream's timestamps about 80 ms behind live, at around 640 px wide. It only runs while the window is visible, so it costs nothing when the app is in the tray. Untick **Show preview** to turn it off completely. The preview shows one field of interlaced sources (1080i/480i), which avoids combing. There's no audio in the preview.

## Recording

**Record** saves the HD PVR's own H.264 stream with no re-encoding and no quality loss. New installs record to `Videos\PaugeChamp` as `PaugeChamp_<date>_<time>`. The folder is set on the **Output** tab. Pick the format in the dropdown next to the button, or in OBS:

| Format | Notes |
|---|---|
| **MP4** (default) | Plays and edits everywhere. The index is written when you stop, so if the PC crashes or loses power mid-recording, that file won't be playable. If the stream restarts during a recording (input change, resolution change, unplug), recording continues in `..._part2.mp4`, `_part3`, and so on. |
| **MPEG-TS (.ts)** | Exactly what the encoder sends. Playable up to the last byte even after a crash. Best for long, unattended captures. To convert later: `ffmpeg -i in.ts -c copy out.mp4`. |

Both formats carry AAC or AC-3 audio, whichever the HD PVR is set to.

## Command line

```
PaugeChamp.exe status
PaugeChamp.exe set video_input=composite audio_input=rca_front bitrate=8 bitrate_mode=vbr peak_bitrate=12
PaugeChamp.exe record start
```
`PaugeChamp.ps1` takes the same commands. Run with `help` for the full list. It talks to the running app over the named pipe `\\.\pipe\paugechamp`, which is restricted to your user and rejects network clients.

## Files

```
src/                  C# sources (.NET Framework 4.8; builds with the csc.exe that ships with Windows)
  HdPvrDevice.cs        USB protocol: authorization, settings, stream start/stop
  DeviceFinder.cs       finds the unit, detects which driver it uses, WinUSB interface fix-up
  BulkReader.cs         overlapped WinUSB reads of the transport stream
  CaptureEngine.cs      device worker thread: reconnect, signal wait, stall recovery, UDP + recording
  TsDemuxer.cs          MPEG-TS -> H.264 / AAC / AC-3 elementary streams
  Mp4Writer.cs          lossless MP4 remuxer
  H264.cs               NAL splitting, SPS parsing
  PreviewDecoder.cs     live preview via the Windows H.264 decoder (Media Foundation)
  MediaFoundation.cs    COM interop for the above
  PipeServer.cs         control channel for OBS / CLI
  MainForm.cs           window (preview + settings tabs) and tray
obs/paugechamp_obs.lua  OBS script
PaugeChamp.ps1          launcher that works with Smart App Control on
build.ps1               builds bin\PaugeChamp.exe
install-shortcut.ps1    Start menu (and optional sign-in) shortcut
```

## Notes

- Latency to OBS is about 1–2 s. That's the HD PVR's hardware encoder and is the same with the Hauppauge software.
- The fan is always switched on at connect. The HD PVR is known to overheat.
- Settings are stored in `%APPDATA%\PaugeChamp\settings.ini`.
- License: the protocol logic follows the GPL-2.0 Linux `hdpvr` driver, so treat this project as GPL-2.0.
