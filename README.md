# AutoMute

A small Windows tray app that mutes your system audio when it gets too loud
(and optionally when there's strong high-pitched content), then unmutes
automatically. A single portable `.exe` with no installer, no runtime and no
dependencies. It runs on any Windows 10 or 11 PC.

## Build (one time, about 5 seconds)

1. Unzip this folder anywhere.
2. Double-click **`build.bat`**.
3. You get **`AutoMute.exe`**. That one file is the whole app, so copy it wherever you like.

`build.bat` uses the C# compiler that ships inside Windows (part of .NET
Framework 4.8), so there is nothing to download. If Windows says the .bat is
from the internet, right-click the zip, choose **Properties**, tick **Unblock**,
then unzip again.

If the build fails, copy the error lines it prints and send them over.

## Use

- Run `AutoMute.exe`. The settings window opens and a speaker icon appears in the tray.
- **Every change applies instantly.** Watch the live meters while audio plays:
  - Click or drag on the **Volume** meter to set the mute threshold (yellow marker).
  - The bar turns amber within 6 dB of the threshold and red above it.
- Closing the window keeps AutoMute running in the tray. Left-click the icon
  to reopen it; right-click for **Monitoring enabled** (pause/resume), **Unmute now**,
  **Start with Windows** and **Exit**.
- Tray icon: blue = monitoring, red X = muted by AutoMute, grey = paused.

## Settings

| Setting | Meaning |
|---|---|
| Mute above | Loudness (dBFS) that triggers a mute. 0 = digital maximum. |
| Averaging | 0 = react to each ~10 ms slice. 100–300 ms ignores short clicks and pops (readings get lower, so lower the threshold to match). |
| Unmute after | Seconds after the last trigger before unmuting. |
| Stay muted while it's still loud | Each new trigger restarts the timer, so it doesn't unmute into the same loud sound. |
| High-pitch trigger | Experimental. Mutes when energy above the cutoff frequency exceeds its own threshold. |

Settings are saved to `AutoMute.ini` next to the exe, so the app stays portable.
If that folder isn't writable, they go to `%APPDATA%\AutoMute\`.

## Behaviour details

- **Never leaves your PC muted.** AutoMute unmutes on Exit, on Windows
  shutdown/sign-out and when the output device changes.
- **Respects your own mute.** If you muted manually, AutoMute leaves it alone.
  If you unmute while AutoMute has you muted, it won't re-mute for 5 seconds.
- **Follows device changes.** It switches automatically when you plug in
  headphones or change the default output.
- **Single instance.** A second copy won't start, so two copies can't fight over mute.
- **Start with Windows** uses a per-user registry entry (no admin rights needed)
  and starts hidden in the tray. If you move the exe, the entry updates itself
  the next time you run it.

## Limitations

- It reacts to audio that is already playing, so the first ~10–30 ms of a
  sudden loud sound gets through before the mute lands.
- Levels are measured on the digital signal, so they don't change when you
  turn the Windows volume slider or your speakers up or down.
- Games or apps that use WASAPI *exclusive mode* bypass the shared mix, and
  AutoMute can't hear them.

## Source layout

| File | Contents |
|---|---|
| `src/CoreAudio.cs` | Hand-written interop for the Windows audio APIs (loopback capture, mute, device notifications) |
| `src/AudioEngine.cs` | Background capture thread, trigger logic, mute/unmute timer |
| `src/Dsp.cs` | Loudness, averaging window, FFT, high-band level, dominant pitch |
| `src/TrayContext.cs` | Tray icon/menu, settings persistence, app lifetime |
| `src/MainForm.cs`, `src/LevelMeter.cs` | Settings window and live meters |
| `src/Settings.cs`, `src/Startup.cs` | INI settings and start-with-Windows |

The code sticks to C# 5 so the compiler built into Windows can build it. It
also compiles unchanged in Visual Studio if you want to work on it there.
