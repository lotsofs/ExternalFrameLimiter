# External Frame Limiter

A small Windows tool that caps GTA San Andreas (PC 1.0) at 30 or 60 FPS, or uncaps it, switched with global hotkeys.

| Key | Frame rate |
| --- | --- |
| F5 | 30 |
| F6 | 60 (the setting at start) |
| F7 | Uncapped |
| Hold F8 for 2 seconds | Switches the game's 14 ms frame delay off, or back on (on at start); see below |
| Hold Num+ | 30 while held, or 60 if the setting is 30; letting go goes back to the setting |

**The 14 ms delay** is part of the game: it makes every other frame 14 ms longer. With it on (the default), the game keeps its own frame timing:
- 30 is the game as it is without the tool: 25 FPS on average, frames alternating between 33 and 47 ms.
- 60 caps the game at 103, so that vsync on a 60 Hz screen shows an even 60. The game's own steps still alternate between about 10 and 23 ms.

With the delay off, 30 and 60 are even, both on screen and in the game, and 60 is a plain cap of 60.

The keys work whichever window has focus, and they still reach the game and LiveSplit. The window shows the game's status, the target, and the frame rate measured from the game's frame counter, with the shortest and longest frame in the last second. Minimise it to send it to the tray.

Frame Limiter must be on in the game's menu. While it is off, the tool does nothing.

With Frame Limiter on, the game waits for vsync, which holds it at the screen's refresh rate. Uncapped switches that vsync off, so the picture may tear. At 30 and 60, vsync stays on.

Start it before or after the game. It finds `gta_sa.exe` or `gta-sa.exe`, and finds it again after a restart. When you close the tool it puts the game back as it was. If the game runs as administrator, run the tool as administrator too.

**Speedrunning:** 30 with the delay on is the game's own timing. With the delay off, 30 is an even 30 FPS, not the vanilla 25 that Speedrun SilentPatch keeps. Check your category's rules before using anything else in a run.

## How it works

The tool only caps the game while **Frame Limiter** is on in the game's menu (the byte at `0xBA6794`, which the main loop checks at `0x748D62`). While it is off, the tool leaves the game as it is without the tool. Every quarter second it writes these into the game's memory, rewriting any that have changed (after a game restart, for example):

- **The cap**, `RsGlobal.frameLimit` at `0xC1704C`. The game's main loop (`0x748D81`) starts the next frame once more than `1000 / cap` ms have passed. The game writes 30 there once, at startup (`0x619622`). Uncapped writes 1000, not higher, because the game's timers freeze above 1000 FPS.
- **The 14 ms frame delay off, once F8 has been held.** `Idle` (`0x53E920`) waits until `ms since the timer update - stored >= 14`, then stores the current value. The compare is unsigned, so the wait happens on every other frame only. With the cap at 30, frames alternate between 33 and 47 ms: an average of 25 FPS. SilentPatch removes this delay; Speedrun SilentPatch keeps it. With the delay kept, a cap of 60 would give about 42 FPS and 120 about 65. The tool changes the `0E` in `cmp eax,0Eh` (`0x53E94C`) to `00`, so the wait loop runs once and doesn't wait. That is a single byte, so it is safe to write while the game runs. If the code there isn't 1.0's `83 F8 0E 72 E1`, it is left alone, and the window says so.
- **The limiter's vsync off, when uncapped.** `psCameraShowRaster` (`0x745240`, reached through `RsCameraShowRaster` at `0x619440` on every frame) presents with vsync when Frame Limiter is on or the byte at `0xBAB318` is set. The tool changes its `jne` (`75 0D` at `0x745247`) to `75 00`, a jump to the next instruction, so only `0xBAB318` still asks for vsync. That is also a single byte.

The frame rate shown comes from `CTimer::m_FrameCounter` (`0xB7CB4C`), which `CTimer::Update` adds one to every frame. It is read about once a millisecond, so frame lengths are accurate to about a millisecond.

The addresses were read from the code of the 1.0 US exe. The version check is the same as SplitSpammer's: a main module of 18313216 bytes, or the int 38079 at `+0x4245BC` or `+0x42457C`.

If the tool is killed instead of closed, the changes stay until the game is restarted.

## Build

```sh
dotnet build ExternalFrameLimiter.csproj -c Release
```

This needs the .NET Framework 4.8.1 targeting pack. The output is `bin\Release\ExternalFrameLimiter.exe`, a single file.
