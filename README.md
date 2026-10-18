# TrayTools

Two Windows tray apps, written in C# and compiled with the .NET Framework `csc.exe` that ships with Windows (no SDK needed).

## VxeBattery.exe
Shows the battery % of a VXE R1 Pro / Pro Max mouse in the tray.
- Receiver `3554:F58A` (wired `3554:F58C`), vendor HID collection usage page `0xFF02`, usage `0x0002`.
- Request: 17-byte output report `08 04 00 … 00 49`. Reply: byte 6 = battery %, byte 7 = wired flag, bytes 8–9 = battery mV (big-endian).
- Protocol from [Fan4Metal/ATK_tray](https://github.com/Fan4Metal/ATK_tray).

## MicFX.exe
Streamer voice chain for a Fantech Leviosa mic, using Equalizer APO.
- Equalizer APO's `config.txt` contains `Include: C:\ProgramData\MicFX\active.txt`. MicFX rewrites that file and Equalizer APO reloads it live.
- Chain: RNNoise (VST2) → gain → EQ (tone controls) → ReaComp compressor → EQ → ReaComp as a −1 dB limiter.
- Presets: Streamer, Natural (no AI denoise), Max noise kill, Bypass. Tone menu: warmth, nasal cut, brightness. Calibrate sets the input gain from 5 s of speech.

## Layout
- `src/` — sources. `Hid.cs` + `MouseBattery.cs` = VxeBattery; `MicFx.cs` = MicFX.
  Dev tools: `VstInspect.cs` (lists VST2 parameter names and value mappings), `WavTool.cs` (test tones, level/gate analysis), `RecRaw.cs` (raw mic recording), `Probe.cs` (HID probe).
- `setup/` — `RUN-SETUP.cmd` / `setup-admin.ps1` (one-time admin install of Equalizer APO 1.4.2, ReaPlugs 2.36, RNNoise v1.10) and a copy of the Equalizer APO `config.txt`. The installers go in `setup/downloads/` (not in the repo, see below).
- `samples/` — A/B renders of the chain variants (local only; not in the repo).

## Installers for MicFX setup
Download these from their official sources and place them where `setup-admin.ps1` expects them:

| Path under `setup/downloads/` | Source |
|---|---|
| `dl_eqapo/EqualizerAPO-x64-1.4.2.exe` | [Equalizer APO](https://sourceforge.net/projects/equalizerapo/files/) |
| `dl_reaplugs/reaplugs236_x64-install.exe` | [ReaPlugs](https://www.reaper.fm/reaplugs/) |
| `dl_rnnoise/x/win-rnnoise/vst/rnnoise_mono.dll`, `rnnoise_stereo.dll` | [noise-suppression-for-voice v1.10](https://github.com/werman/noise-suppression-for-voice/releases) (`win-rnnoise.zip`, extracted into `dl_rnnoise/x/`) |

## Build
```
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
%CSC% /target:winexe /out:VxeBattery.exe /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll src\Hid.cs src\MouseBattery.cs
%CSC% /target:winexe /codepage:65001 /out:MicFX.exe /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll src\MicFx.cs
%CSC% /platform:x64 /out:src\VstInspect.exe src\VstInspect.cs
```

## Status
- **VxeBattery: in use.** Runs from this folder at startup. Digits use a 3×5 pixel-block font scaled by whole pixels (`Glyphs` in `MouseBattery.cs`).
- **MicFX: shelved.** Equalizer APO, ReaPlugs and RNNoise were uninstalled. To use it again, run `setup\RUN-SETUP.cmd` and then `MicFX.exe`.

## Lessons
- The Leviosa's audio function threw Code 10 on a front USB port. It worked on a rear port.
- Robotic sound came from low signal-to-noise (about 10 dB) at the mic, not from the chain. Get closer to the mic before tuning software.
