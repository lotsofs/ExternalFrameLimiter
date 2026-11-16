# External Frame Limiter

A small Windows tool that caps GTA San Andreas (PC 1.0) at an even 30, 60 or 120 FPS, switched with global hotkeys.

| Key | Frame rate |
| --- | --- |
| F5 | 30 |
| F6 | 60 (the setting at start) |
| F7 | 120 |
| Hold Num+ | 30 while held, or 60 if the setting is 30; letting go goes back to the setting |

The keys work whichever window has focus, and they still reach the game and LiveSplit. The window shows the game's status, the target, and the frame rate measured from the game's frame counter, with the shortest and longest frame in the last second. Minimise it to send it to the tray.

Frame Limiter must be on in the game's menu. While it is off, the tool does nothing.

With Frame Limiter on, the game waits for vsync, which holds it at the screen's refresh rate. So while the target is above the refresh rate (120 on a 60 Hz screen), the tool switches that vsync off, and the picture may tear. At or below the refresh rate, vsync stays on.

Start it before or after the game. It finds `gta_sa.exe` or `gta-sa.exe`, and finds it again after a restart. When you close the tool it puts the game back as it was. If the game runs as administrator, run the tool as administrator too.

**Speedrunning:** at 30 this gives an even 30 FPS, not the vanilla 25 that Speedrun SilentPatch keeps. Check your category's rules before using it in a run.

## How it works

The tool only caps the game while **Frame Limiter** is on in the game's menu (the byte at `0xBA6794`, which the main loop checks at `0x748D62`). While it is off, the tool leaves the game as it is without the tool. Every quarter second it writes two things into the game's memory, rewriting any that have changed (after a game restart, for example):

- **The cap**, `RsGlobal.frameLimit` at `0xC1704C`. The game's main loop (`0x748D81`) starts the next frame once more than `1000 / cap` ms have passed. The game writes 30 there once, at startup (`0x619622`).
- **The 14 ms frame delay off.** `Idle` (`0x53E920`) waits until `ms since the timer update - stored >= 14`, then stores the current value. The compare is unsigned, so the wait happens on every other frame only. With the cap at 30, frames alternate between 33 and 47 ms: an average of 25 FPS. SilentPatch removes this delay; Speedrun SilentPatch keeps it. With the delay kept, a cap of 60 would give about 42 FPS and 120 about 65. The tool changes the `0E` in `cmp eax,0Eh` (`0x53E94C`) to `00`, so the wait loop runs once and doesn't wait. That is a single byte, so it is safe to write while the game runs. If the code there isn't 1.0's `83 F8 0E 72 E1`, it is left alone, and the window says so.

- **The limiter's vsync off, above the refresh rate.** `psCameraShowRaster` (`0x745240`, reached through `RsCameraShowRaster` at `0x619440` on every frame) presents with vsync when Frame Limiter is on or the byte at `0xBAB318` is set. The tool changes its `jne` (`75 0D` at `0x745247`) to `75 00`, a jump to the next instruction, so only `0xBAB318` still asks for vsync. That is also a single byte.

The frame rate shown comes from `CTimer::m_FrameCounter` (`0xB7CB4C`), which `CTimer::Update` adds one to every frame. It is read about once a millisecond, so frame lengths are accurate to about a millisecond.

The addresses were read from the code of the 1.0 US exe. The version check is the same as SplitSpammer's: a main module of 18313216 bytes, or the int 38079 at `+0x4245BC` or `+0x42457C`.

If the tool is killed instead of closed, the changes stay until the game is restarted.

## Build

```sh
dotnet build ExternalFrameLimiter.csproj -c Release
```

This needs the .NET Framework 4.8.1 targeting pack. The output is `bin\Release\ExternalFrameLimiter.exe`, a single file.
