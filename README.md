# Live2DPetCore

**The pet, the frame pacer and the quality governor — and nothing else.**

This is a cut, not a mode: the part everything else was built on, on its own, so it
can be read, changed and shipped without anything else having to agree with it.

## What the one promise is

Keep the character moving at a frame rate on hardware that cannot do everything at
once, by giving up **render quality** rather than frames.

Measured with the software rasteriser — a machine with no GPU at all — at
3840×2160: the governor steps the render scale from `1.00` to `0.85` after 120
frames that came in at a median of 19.5 ms, and the median falls back to 15.5 ms
against the 16.67 ms budget. A higher setting misses the budget; a lower one is
softer for nothing. That decision is the whole program.

## What is in it

| area | files | why it has to be here |
|---|---|---|
| the model | `Cubism/`, `Model/`, `Motion/`, `Physics/` | parameters, motions, expressions, cloth and hair physics |
| the picture | `Rendering/` | Direct3D 11 + DirectComposition, batching, clipping masks, the offscreen upscale the ladder pulls |
| the life | `Animation/` | blink, breath, gaze at the cursor |
| the desktop | `Platform/` | a transparent top-most window shaped to the artwork, the tray icon and menu, the frame pacer |
| the promise | `App/QualityGovernor.cs`, `App/FrameStats.cs` | the ladder, and the two signals it is allowed to steer by |
| the loop | `CoreRunner.cs` | brings them up in the order they depend on each other, then owns the one loop |

## What is deliberately not in it

The interaction editor. The named pipe. The rules file (`*.interaction.json`) —
clicks here use the Cubism convention instead: a motion group named after the
region (`TapHead`), else the usual tap, else anything that is not the idle loop.
Sequences, presets, the timeline, hotkeys and expression layers. The Windows voice,
the voice pipeline and the bridge to an external VTuber stack. Face capture. Screen
reading. The wheel that resizes the pet. The size slider. Model switching.

Every one of those was built on top of the pipe or the editor rather than inside
the renderer — which is why cutting them costs this build nothing it needs, and
why a fuller build can keep them.

## Build and run

```powershell
dotnet build -c Release
bin\Release\net9.0-windows\win-x64\Live2DPetCore.exe --model <path to .model3.json>
```

No model ships with this. Point `--model` at one, or drop a folder of them under
`models\` beside the executable and the first `*.model3.json` in there is used.
Start it with no arguments and no model and it says so and exits rather than
inventing a path.

The only thing beside the executable that is not ours is Live2D's Cubism Core
(`Live2DCubismCore.dll`, with its own licence file). It is freely redistributable
under Live2D's terms; see `third_party/LICENSE-Live2D-CubismCore.md`.

## Licence

MIT, for the code in this folder — see `LICENSE`. That does not extend to
`third_party/` (Live2D's Cubism Core, under Live2D's terms) or to any model,
motion or artwork you load into it.

## Flags that matter

```
--model <path>      the .model3.json
--min-fps <n>       the floor the governor defends (default 60) - the promise
--fps <n>           the ceiling (default: the display's refresh rate, 0 = none)
--render-scale <f>  pin the quality and stand the governor down
--governor <on|off> trade quality for frame rate (default on)
--width/--height    window size
--exit-after <sec>  run for a while and quit
```

And the measurement set: `--warp` (software rasteriser — a machine with no GPU),
`--cpu-affinity <n>`, `--thread-priority <0|1|2>`, `--no-shape`, `--no-batch`,
`--no-model`, `--no-render`, `--no-clip`, `--no-present`, `--no-vsync`,
`--hit-test-grid <n>`, `--capture <path>`.

## Size, for scale

| | files | lines of C# |
|---|---|---|
| Live2DPetCore (this folder) | 47 | 9,129 |
| the fuller build it was cut from | 65 | ~22,200 |
