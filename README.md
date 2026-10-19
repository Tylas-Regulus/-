# Live2DPetCore

**The pet, the frame pacer and the quality governor — and nothing else.**

This is the lean, source-only cut of the Live2DPet engine: the model, the
renderer, the transparent top-most window, animation, physics, the quality
governor, atlas page-size control and the UV watermark rules. It builds as a
normal .NET desktop app or as a native AOT single-file executable.

## What is in it

| area | files | why it has to be here |
|---|---|---|
| the model | `Cubism/`, `Model/`, `Motion/`, `Physics/` | parameters, motions, expressions, cloth and hair physics |
| the picture | `Rendering/` | Direct3D 11 + DirectComposition, batching, clipping masks, the optional offscreen upscale, texture page sizes and lazy pages |
| the life | `Animation/` | blink, breath, gaze at the cursor |
| the desktop | `Platform/` | a transparent top-most window shaped to the artwork, the tray icon and menu, the frame pacer |
| the promise | `App/QualityGovernor.cs`, `App/FrameStats.cs` | the quality ladder and the two signals it is allowed to steer by |
| the loop | `CoreRunner.cs` | brings them up in the order they depend on each other, then owns the one loop |
| the watermark | `Rendering/UvDrop.cs` | drops triangles by UV, so a signature can be removed without touching the model file |

It has no editor and no config page. It also has no named pipe, no
`*.interaction.json` rules file, no sequences, presets, timeline, hotkeys or
expression layers. Clicks use the Cubism convention instead: a motion group
named after the region (`TapHead`), else the usual tap, else anything that is
not the idle loop. There is no voice/TTS bridge, face capture, screen reading,
wheel resizing, size slider or model switching.

## Build and run

JIT (the fast build while changing code):

```powershell
dotnet build Live2DPetCore.csproj -c Release
bin\Release\net9.0-windows\win-x64\Live2DPetCore.exe --model <path to .model3.json>
```

Native AOT single file:

```powershell
dotnet publish Live2DPetCore.Aot.csproj -c Release -r win-x64 -o publish\aot
publish\aot\Live2DPetCore.exe --model <path to .model3.json>
```

The AOT output is `Live2DPetCore.exe` plus `Live2DCubismCore.dll`.
The `.pdb` is only for local debugging and should not be uploaded.

No model ships with this. Point `--model` at one, or drop a folder of them under
`models\` beside the executable and the first `*.model3.json` in there is used.
Start it with no arguments and no model and it says so and exits rather than
inventing a path.

## Texture page size

Atlas pages are authored for zooming and 4K displays; a 4096 page is often
several times the texel density a desktop pet can show. `--texture-size` caps
the long edge of every page:

- `0` (or no flag) keeps the model's authored size;
- `4096`, `2048`, `1024` are ceilings, never upscales.

The same value can be written to the state file as `textureSize=1024`. A running
pet re-reads the state about twice a second and reloads the pages that are
already resident in place; deferred pages only remember the new cap. The
reload is a one-time pause of about 2-3 seconds, not a restart.

On a 17-page 4096 test model, the texture bytes are 1088 MiB at 4096, 272 MiB
at 2048 and 68 MiB at 1024. The core's own window is 640x900, so its process
totals are smaller than the full engine's; the texture budget is the part that
scales with this setting. The hit-test alpha plane is stored at a quarter of the
page on each axis, so it shrinks with the page as well.

`--lazy-textures` (state `lazyTextures=true`) keeps pages on disk until the
current pose needs them. It is independent of the page size: a 1024 page is
small enough to keep all of them resident, while a 4096 model with props may
benefit from both.

## Watermark rules

Some models carry a signature in artwork that is shared with real parts. Two
rules remove it without editing the model:

```powershell
--hide-part PartSignature --hide-part PartWatermark
--drop-uv "ArtMeshSignature:0.1200,0.5750,0.1970,0.9560"
```

`--hide-part` leaves a whole part or drawable out. `--drop-uv` leaves only the
triangles whose UV centroid falls inside the rectangle, which is what handles a
watermark that shares one drawable with a collar or hair. The rules are matched
by substring and remembered per model in the state file.

## Flags that matter

```
--model <path>      the .model3.json
--width/--height    window size in pixels (default 640x900)
--fps <n>           frame ceiling (default: the display's refresh rate, 0 = none)
--min-fps <n>       the floor the governor defends (default 60)
--render-scale <f>  pin the render quality, 0.2 to 1 (stands the governor down)
--governor <on|off> trade render quality for frame rate (default on)
--auto-plan <quality|framerate>  which lever automatic mode spends first
--lazy-textures     keep atlas pages on disk until the picture needs them
--texture-size <px> cap each atlas page's long edge (0 = as authored)
--hide-part <name>  leave a part/drawable out; repeatable
--drop-uv <spec>    leave a UV patch out (drawable:u0,v0,u1,v1)
--no-motion         do not play motions
--no-fx             no blink, breath or gaze
--no-physics        no physics
--exit-after <sec>  quit by itself, 0 = run until closed
--log <path>        where to write the run log
--state <path>      where to keep position, quality, page size and rules
--window-position <x,y>
--click-through     the pet takes no mouse input at all
--tray <on|off>     the tray icon (default on)
--capture <path>    write one frame to a 32-bit BMP
```

Measurement: `--warp`, `--cpu-affinity <n>`, `--thread-priority <0|1|2>`,
`--no-shape`, `--no-batch`, `--no-model`, `--no-render`, `--no-clip`,
`--no-present`, `--no-vsync`, `--mask-every-frame`, `--hit-test-grid <n>`.

## Licence

MIT, for the code in this folder — see `LICENSE`. That does not extend to
`third_party/` (Live2D's Cubism Core, under Live2D's terms) or to any model,
motion or artwork you load into it.

## Size

| | files | lines of C# |
|---|---:|---:|
| Live2DPetCore (this folder) | 49 | 10,963 |
