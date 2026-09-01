# Recipes

Three worked effects, all built and verified in Unity 6000.5 / URP. Node names are written
loosely on purpose — that is how they are meant to be written.

## Textures

Outputs fall back to Unity's `DefaultDot` when no texture is assigned — a glowing sphere with a
defined edge, which is why untextured particles read as a string of beads rather than anything
continuous. Assign one deliberately.

The slot name differs by output, so check with `inspect` rather than assuming: it is
`mainTexture` on `Output Particle (Unlit) Quad`, and `_BaseMap` on the Shader Graph strip output.

Run a `textures` job before reaching for an image editor — the skill ships a CC0 library
(`assets/Textures/`, installed into `Assets/Textures/`) covering smoke, fire, sparks, magic,
traces, impacts and 14 animated flipbook sheets. `references/textures.md` maps intent to file,
and explains the one thing the file name cannot tell you: whether the shape lives in the alpha
channel (tintable, any blend mode) or in greyscale RGB with alpha solid (Additive only, a black
square on `Alpha`).

Generate a PNG only when nothing in the library fits — a soft radial falloff with alpha driven to
exactly zero at the rim has no edge for overlapping particles to reveal, and a band that is soft
across its width but uniform along its length stretches down a ribbon without seams.

## Spark burst

Small, fast, hot. The two things that make it read as sparks rather than confetti: additive
blending, and stretching the quads along their velocity.

```json
{
  "path": "Assets/VFX/Sparks.vfx",
  "mode": "create",
  "graph": {
    "systems": [{
      "name": "Sparks",
      "contexts": [
        { "id": "spawn", "node": "Spawn",
          "blocks": [ { "node": "Constant Spawn Rate", "values": { "Rate": 200 } } ] },

        { "id": "init", "node": "Initialize Particle",
          "settings": { "capacity": 2048 },
          "blocks": [
            { "node": "Set Lifetime (Random Uniform)", "values": { "A": 1.2, "B": 3.0 } },
            { "node": "Set Position Shape (Sphere)",   "values": { "radius": 0.15 } },
            { "node": "Set Velocity (Random Uniform)",
              "values": { "A": [-2, 1, -2], "B": [2, 5, 2] } },
            { "node": "Set Size (Random Uniform)", "values": { "A": 0.012, "B": 0.035 } },
            { "node": "Set Scale", "values": { "_Scale": [0.3, 2.6, 1.0] } },
            { "node": "Set Color", "values": { "_Color": [4.0, 1.4, 0.25] } }
          ] },

        { "id": "update", "node": "Update Particle",
          "blocks": [
            { "node": "Gravity",     "values": { "Force": [0, -6, 0] } },
            { "node": "Linear Drag", "values": { "dragCoefficient": 0.6 } }
          ] },

        { "id": "output", "node": "Output Particle (Unlit) Quad",
          "settings": { "blendMode": "Additive" },
          "values": { "mainTexture":
            "Assets/Textures/brackeys_vfx_bundle/particles/alpha/spark_04_a.png" },
          "blocks": [
            { "node": "Orient (Along Velocity)" },
            { "node": "Set Alpha (Over Life)",
              "values": { "Alpha": { "keys": [ {"time":0,"value":0}, {"time":0.1,"value":1},
                                               {"time":1,"value":0} ] } } }
          ] }
      ],
      "flow": [["spawn","init"], ["init","update"], ["update","output"]]
    }]
  }
}
```

Colour components above 1 are what produce glow under bloom. `Set Scale` with an elongated Y,
combined with `Orient (Along Velocity)`, turns round dots into streaks.

## Embers with GPU-event motes

Two systems: slow rising embers, each of which spits smaller motes through a GPU event. Also
shows an operator chain and an exposed parameter driving spawn rate.

Structure, abbreviated — see the spec reference for the full shape:

```json
{
  "parameters": [
    { "id": "pRate", "name": "EmberRate", "type": "float", "value": 40.0, "category": "Tuning" }
  ],
  "operators": [
    { "id": "opTime", "node": "Periodic Total Time", "values": { "Period": 4.0 } },
    { "id": "opSin",  "node": "Sine" },
    { "id": "opMul",  "node": "Multiply" }
  ],
  "systems": [
    { "name": "Embers", "contexts": [
        { "id": "spawn", "node": "Spawn",
          "blocks": [ { "id": "rateBlock", "node": "Constant Spawn Rate", "values": { "Rate": 40 } } ] },
        { "id": "init", "node": "Initialize Particle", "settings": { "capacity": 4096 },
          "blocks": [
            { "node": "Set Lifetime (Random Uniform)", "values": { "A": 2.0, "B": 5.0 } },
            { "node": "Set Position Shape (Sphere)", "values": { "radius": 1.5 } },
            { "node": "Set Color", "settings": { "Composition": "Blend" },
              "values": { "_Color": [3.0, 0.9, 0.2], "Blend": 0.75 } }
          ] },
        { "id": "update", "node": "Update Particle",
          "blocks": [
            { "node": "Turbulence", "values": { "Intensity": 0.8, "frequency": 0.6, "octaves": 3 } },
            { "id": "trigger", "node": "Trigger Event (Over Time)", "values": { "Rate": 3 } }
          ] },
        { "id": "output", "node": "Output Particle (Unlit) Quad",
          "settings": { "blendMode": "Additive" },
          "blocks": [
            { "node": "Set Color (Over Life)", "values": { "Color": {
                "colorKeys": [ {"color":[1.0,0.95,0.6],"time":0.0},
                               {"color":[1.0,0.35,0.05],"time":0.35},
                               {"color":[0.25,0.03,0.0],"time":1.0} ],
                "alphaKeys": [ {"alpha":0.0,"time":0.0}, {"alpha":1.0,"time":0.12},
                               {"alpha":0.0,"time":1.0} ] } } },
            { "node": "Set Size (Over Life)", "values": { "Size": {
                "keys": [ {"time":0,"value":0.3}, {"time":0.2,"value":1.0}, {"time":1,"value":0} ] } } }
          ] }
      ],
      "flow": [["spawn","init"], ["init","update"], ["update","output"]] },

    { "name": "Motes", "contexts": [
        { "id": "gpuevt",  "node": "GPU Event" },
        { "id": "minit",   "node": "Initialize Particle", "settings": { "capacity": 1024 } },
        { "id": "mupdate", "node": "Update Particle" },
        { "id": "moutput", "node": "Output Particle (Unlit) Quad",
          "settings": { "blendMode": "Additive" } }
      ],
      "flow": [["gpuevt","minit"], ["minit","mupdate"], ["mupdate","moutput"]] }
  ],
  "links": [
    { "from": "opTime", "to": "opSin", "toSlot": "x" },
    { "from": "opSin",  "to": "opMul", "toSlot": "a" },
    { "from": "pRate",  "to": "opMul", "toSlot": "b" },
    { "from": "opMul",  "to": "rateBlock", "toSlot": "Rate" },
    { "from": "trigger", "fromSlot": "evt", "to": "gpuevt", "toSlot": "evt" }
  ]
}
```

The GPU event connection is a **data link** from the trigger block's `evt` output to the GPU
Event context's `evt` input — not a flow link. The flow link is `gpuevt → minit`.

## World-space ribbon trail

Settled proportions, after a long debugging session: **30 spawns/sec, 2.5s lifetime, 75
particles**, buffer matched exactly, `sort: Off`, `tilingMode: Stretch`, `Orient: Face Camera
Position` in the output, and alpha ramped from zero at birth. The rate is the number that matters
most — above frame rate the trail breaks in ways that look like a texture or geometry problem but
are neither.

```json
{
  "path": "Assets/VFX/Trail.vfx",
  "mode": "create",
  "graph": {
    "systems": [{
      "name": "RibbonTrail",
      "contexts": [
        { "id": "spawn", "node": "Spawn",
          "blocks": [ { "node": "Constant Spawn Rate", "values": { "Rate": 30 } } ] },

        { "id": "init", "node": "Initialize Particle Strip",
          "space": "World",
          "settings": { "capacity": 75, "stripCapacity": 1, "particlePerStripCount": 75 },
          "blocks": [
            { "node": "Set Lifetime", "values": { "Lifetime": 2.5 } },
            { "node": "Set Position", "values": { "Position": { "space": "Local", "value": [0,0,0] } } },
            { "node": "Set Size", "values": { "Size": 0.22 } },
            { "node": "Set Color", "values": { "_Color": [0.55, 0.32, 0.95] } }
          ] },

        { "id": "update", "node": "Update Particle" },

        { "id": "output", "node": "Output ParticleStrip (Shader Graph) Quad",
          "settings": { "tilingMode": "Stretch", "sort": "Off" },
          "values": { "_BaseMap": "Assets/VFX/Textures/TrailBand.png" },
          "material": { "_Blend": 0, "_Surface": 1, "_SrcBlend": 5, "_DstBlend": 10, "_ZWrite": 0 },
          "blocks": [
            { "node": "Orient (Face Camera Position)" },
            { "node": "Multiply Size (Over Life)",
              "values": { "Size": { "keys": [ {"time":0,"value":1.0}, {"time":0.65,"value":0.55}, {"time":1,"value":0} ] } } },
            { "node": "Multiply Color (Over Life)",
              "values": { "Color": {
                  "colorKeys": [ {"color":[1,1,1],"time":0}, {"color":[1,1,1],"time":1} ],
                  "alphaKeys": [ {"alpha":0.0,"time":0.0}, {"alpha":0.35,"time":0.06},
                                 {"alpha":0.85,"time":0.18}, {"alpha":0.55,"time":0.6},
                                 {"alpha":0.0,"time":1.0} ] } } }
          ] }
      ],
      "flow": [ ["spawn","init"], ["init","update"], ["update","output"] ]
    }]
  }
}
```

Optionally pinch both ends of the strip so a buffer wrap cannot show a seam: add a
`Get Ratio Over Strip [0..1]` operator and a second `Multiply Size (Over Life)` block with
`SampleMode: Custom`, linking the ratio into its `SampleTime`, with a curve that is 0 at both
ends. Unity's `Head_Trail` does the same thing with a manual index/count operator chain.

## Earlier iteration of the ribbon trail

The one that is easy to get wrong. A trail is a ribbon *left behind a moving object*, which
means world-space simulation and an emitter in motion. Build it and then place it on a mover,
or there is nothing to see.

```json
{
  "path": "Assets/VFX/Trail.vfx",
  "mode": "create",
  "graph": {
    "systems": [{
      "name": "RibbonTrail",
      "contexts": [
        { "id": "spawn", "node": "Spawn",
          "blocks": [
            { "node": "Constant Spawn Rate", "values": { "Rate": 90 } },
            { "node": "Increment Strip Index On Start", "values": { "StripMaxCount": 4 } }
          ] },

        { "id": "init", "node": "Initialize Particle Strip",
          "space": "World",
          "settings": { "capacity": 512, "stripCapacity": 4, "particlePerStripCount": 96 },
          "blocks": [
            { "node": "Set Lifetime", "values": { "Lifetime": 1.6 } },
            { "node": "Set Position", "values": { "Position": { "space": "Local", "value": [0,0,0] } } },
            { "node": "Set Size (Random Uniform)", "values": { "A": 0.10, "B": 0.18 } }
          ] },

        { "id": "update", "node": "Update Particle",
          "blocks": [
            { "node": "Set Size (Over Life)", "values": { "Size": {
                "keys": [ {"time":0,"value":1.0}, {"time":0.6,"value":0.6}, {"time":1,"value":0} ] } } }
          ] },

        { "id": "output", "node": "Output ParticleStrip (Shader Graph) Quad" }
      ],
      "flow": [["spawn","init"], ["init","update"], ["update","output"]]
    }]
  }
}
```

Then make it visible:

```json
{ "asset": "Assets/VFX/Trail.vfx", "name": "Trail Preview", "position": [0,1,0],
  "mover": { "motion": "Orbit", "radius": 3.0, "speed": 1.2, "height": 1.0 } }
```

Points that matter:

- `"space": "World"` on Initialize, with a **Local** `[0,0,0]` position. Without the world space
  the ribbon follows the object instead of trailing it; without the local position the particles
  are born at the world origin instead of at the emitter.
- `StripMaxCount` must match `stripCapacity`. Mismatched, strips get recycled unevenly.
- No randomizing position block. Random positions make the ribbon zigzag between random points —
  this is the single most common way to ruin a strip system.
- Keep turbulence low or absent. Strips show every jitter as a kink.
- Unity retypes `Update Particle` to `Update Particle Strip` automatically once the data type is
  ParticleStrip; that is expected, not an error.
- Strips face the camera fine with `Orient: Face Camera Position` — `compatibleData: Particle`
  is a bitmask that ParticleStrip satisfies. They still overlap at sharp bends, which Unity's own
  samples do too; narrower ribbons and gentler paths reduce it, nothing removes it.

## Flipbook smoke

Taken from Unity's `Smoke.vfx` (VisualEffectGraph Additions sample) — the reference for anything
soft. A soft dot cannot be smoke; the flipbook is what supplies an evolving silhouette.

- Output: `Output Particle|Unlit|Octagon`. The octagon's `cropFactor: 0.293` trims the
  transparent border and cuts overdraw on a texture that is mostly empty.
- `uvMode: "Flipbook"`, `flipbookLayout: "Texture2D"`, `flipbookBlendFrames: true`,
  `flipBookSize` 8×8, texture `wispy_smoke_03_8x8.tga` from the bundled library (Unity's own
  sample uses `WispySmoke03b_8x8`; same 8×8 layout).
- `blendMode: "Alpha"` for dark smoke that occludes; `"Additive"` for glow. Additive cannot
  darken, so anything meant to read as shadow or soot must be Alpha.
- Initialize: `|Set|_Tex Index|Random Uniform` A 0 B 63, or every particle animates in lockstep.
- Update: `Flipbook Player`, `mode: "FrameRate"` — 8–14 fps for slow ooze, 20–32 for energy.
- Optional `|Set|_Scale|Random Per Component` for non-uniform squash so blobs are not all discs.

## Flame wisps

From Unity's `Flames.vfx`. Reads as licking energy rather than a smear.

- Output: `Output Particle|Unlit|Quad`, `flipbookBlendFrames: true`. Unity's sample uses
  `Flame03_Temp_16x5` at `flipBookSize` 16×5; the bundled `flame_01_16x4.tga` is 16×4. Set the
  size to match the sheet you actually assigned - a wrong grid slices frames in half.
- `blendMode: "AlphaPremultiplied"` — bright core adds, edges still blend, so it glows without
  blowing out to a white blob.
- `Orient|Fixed Axis` with `Up.direction: [0,1,0]` so flames stay upright instead of tumbling.
- `|Set|_Pivot` at `[0, -0.39, 0]` so each licks upward **from its base** rather than expanding
  about its centre. This is the detail that sells it.
- Tex index random over the real frame count (0–79 for the 16×5 sample, 0–63 for `flame_01_16x4`);
  Flipbook Player around 30 fps.

Tint the whole thing by setting `_Color` — a violet flame reads as cursed energy, a cyan one as
arcane. HDR components above 1.0 let bloom catch it.

## Solid geometry: rays, cones, rings

When no texture will do — anything that must look like a solid volume rather than a card.

- `Output Particle|Unlit|Mesh` with `cullMode: "Off"` (thin geometry stays visible from inside)
  and `zWriteMode: "Off"` (additive layering instead of z-fighting).
- Pin `|Set|_Size` to `1.0` so `|Set|_Scale` is the sole driver.
- Meshes: `ST_FXBase_Cone.fbx` for shafts and cones, `ST_Tube.fbx` for hoops and beams (both in
  the Learning Templates sample). Expose the mesh as a `"type": "mesh"` parameter.
- **Rays**: spawn on the surface of a near-zero-height cylinder (`height: 0.02`) so they form a
  ring; scale thin on X/Z and long on Y; random Y angle 0–360 spreads them, ±14° on X/Z fans them
  out. Drive only `Scale_y` in `|Multiply|_Scale|Over Life` so they extend rather than inflate.
- **Rings**: flatten the tube (`_Scale.y` small), rise at constant velocity, expand X/Z over life.
  Rate × lifetime sets how many are visible and their spacing.
- **A single steady shape**: spawn ~2/s with a fixed lifetime so two or three overlap and
  cross-fade; it reads as one continuous form that breathes instead of discrete pops. Add
  `|Set|_Angular Velocity` (slot `_AngularVelocity`) for a slow spin.
- Do **not** use `Set Pivot` here — see the non-uniform scale rule in SKILL.md. Offset with a
  `|Set|_Position` in Initialize instead.

## Sanity checks before declaring success

Run `inspect` and confirm the values are present — a build reporting `ok` only means it
compiled. Then look for these, which have all bitten before:

- every `Over Life` block is in the **Output** context, never Update
- `boundsMode` is `Manual` with a box covering the effect's travel, never `Automatic`
- output `blendMode` is what the effect needs, not the `Alpha` default
- HDR colours where glow is wanted (components > 1)
- alpha comes from the Color parameter via a `Swizzle` mask `w`, not a separate float
- the output has a real texture or mesh, not the `DefaultDot` fallback
- the texture's shape is in its alpha channel, or the output is Additive — an opaque-alpha
  luminance texture on an `Alpha` output renders as a black square (`textures` reports `hasAlpha`)
- `flipBookSize` matches the sheet's real grid, and `Tex Index` randomisation is capped at
  `columns × rows − 1`
- lifetimes and rates giving a sensible live particle count against `capacity`
- for strips: no random position, matched strip counts, `sort: Off`, spawn rate at or below
  frame rate, alpha ramping from zero, world space if it is a trail
- composite values landed as vectors, not silently skipped (the log tells you)
- no `SKIPPED` lines in the log — each one names the valid slot options
