---
name: unity-vfx-graph
description: Create and modify Unity Visual Effect Graph (.vfx) assets directly inside a Unity project — building particle systems, trails, explosions, smoke, magic effects, sparks and the like from a description, or editing effects that already exist. Works by installing a small editor bridge into the Unity project and driving it with job files, so effects are really authored and compiled by Unity rather than described in prose. Use this whenever someone wants a VFX Graph effect made, tweaked, explained, or debugged; whenever a .vfx file, VisualEffect component, VFX Graph node, block, context or exposed property comes up; and whenever someone asks for particles, a trail, an explosion, embers, magic or similar in a Unity project — even if they never say "VFX Graph" by name. Ships a CC0 particle and flipbook texture library with it, and can install it, audit and repair texture import settings, and assemble flipbook sheets from loose frames — so also use it for particle textures, sprite sheets and flipbooks in a Unity project.
---

# Unity Visual Effect Graph authoring

Author real `.vfx` assets in a Unity project: create new effects from a description, edit
existing ones, read back what an effect currently does, and place effects in the scene to be
looked at.

## Why this works the way it does

Unity ships **no public API for building VFX graphs**. Unity staff have said so directly:
*"We don't have public / documented API to directly create nodes via script or edit VFX graph
assets."* The documented surface is runtime-only — the `VisualEffect` component, property
binders, `ExposedProperty`. Every authoring type in `UnityEditor.VFX` (`VFXGraph`, `VFXContext`,
`VFXBlock`, `VFXSlot`, `VFXLibrary`) is `internal` to `Unity.VisualEffectGraph.Editor`.

So this skill installs a small C# bridge into the project that reaches those internals through
an assembly-definition reference, and exposes them as a file-based job queue. Two consequences
shape everything below:

- **Never guess a node name, setting name or slot path.** Dump the catalog and look it up. The
  internal names are nothing like the UI labels.
- **Never trust a build log alone.** Unity compiles the asset; `inspect` reads back what
  actually landed. A block whose value silently failed to bind still reports a successful build.

## Paths and shell

Unity projects are usually on Windows while the shell you drive them from may not be. Two
separate path conventions are in play and mixing them is the easy mistake:

- **Inside job JSON**, every asset path is project-relative with forward slashes and starts with
  `Assets/` — `"path": "Assets/VFX/Torch.vfx"`. Never a drive letter, never a backslash.
- **In shell commands**, use whatever path reaches the project from the shell you are actually
  running in — a Windows path (`C:/Games/MyGame`; forward slashes work, or escape as
  `C:\\Games\\MyGame`), a POSIX mount of one, or a native path on macOS or Linux. Establish
  it once by listing the project root rather than assuming a layout.

Set a variable once and reuse it, so the examples below transplant cleanly:

```bash
P="<however this shell reaches the project root>"
printf '{}' > "$P/VFXAI_Jobs/001.ping.job"
```

The examples are written for a POSIX shell and use `printf`, `awk` and a Python interpreter.
Check what this environment actually has before leaning on any of them — `python3` in particular
is frequently absent on Windows where `python` works, and PowerShell needs different quoting than
the heredocs below. None of it is load-bearing: the protocol is just files, so any way of writing
a text file and reading one back will do.

## Before anything else: is the bridge alive?

Read `references/install.md` and install the tooling if `<project>/Assets/VFXAI/` is missing.

Then confirm it is running by writing a job and reading the answer:

```bash
printf '{}' > "$P/VFXAI_Jobs/001.ping.job"
for i in $(seq 1 10); do
  [ -f "$P/VFXAI_Results/001.ping.result.json" ] && break
  sleep 1
done
cat "$P/VFXAI_Results/001.ping.result.json"
```

A healthy reply looks like `{"status":"ok","bridgeVersion":"...","kernelLoaded":true}`. If
nothing appears within ~10s, Unity is closed or the bridge is switched off in
**Tools > VFX AI > Control Panel**. If `kernelLoaded` is false, the C# failed to compile —
read `<project>/VFXAI_Reports/compile_log.json`, which reports errors even when the rest of the
tooling is broken.

## The loop

1. **Ping** — confirm the bridge answers.
2. **Catalog** — if `VFXAI_Reports/catalog_index.tsv` is missing or the Unity version changed,
   run a `catalog` job. It enumerates every node this install actually registers.
3. **Textures** — run a `textures` job before designing the effect, not after it looks wrong.
   The silhouette is the first decision, and what the project has (or does not have) changes what
   is worth building. Install the bundled library if there is nothing suitable.
   `references/textures.md`.
4. **Look up** the exact nodes, settings and slot paths you intend to use (see below).
5. **Inspect first when editing.** An `apply` in `edit` mode addresses nodes by ids that come
   from an `inspect` run — `c0`, `c0.b1`, `o2`, `p0`. Always inspect immediately before editing;
   ids shift when the graph is restructured.
6. **Apply** — write the job, wait, read the result. Read every line of `log`: skipped values
   and rejected blocks are reported there, and each skip lists the valid options.
7. **Verify with `inspect`** — confirm the values you set are actually present.
8. **Show the person** — `scene` places the effect on a GameObject, optionally with a mover.
   Offer to open the graph. Then ask how it looks; you cannot see the result yourself.

Jobs that modify the project (`apply`, `scene`, `texconfig`, `flipbook`) wait for human approval
in the control panel unless auto-approve is switched on. That gate is the point — describe what a
job will do before queueing it, and tell the person they need to approve it.

## Finding the right node

Node names in the model are pipe-delimited with sort-order prefixes, e.g.
`|Set|_Lifetime|Random Uniform` in category `#1Set`. Names are matched loosely, so
`"Set Lifetime (Random Uniform)"` resolves fine — but only if such a node exists. Search the
index rather than inventing one:

```bash
# what contexts exist?
awk -F'\t' '$1=="context"' VFXAI_Reports/catalog_index.tsv | cut -f2,3

# blocks mentioning turbulence
awk -F'\t' '$1=="block" && tolower($3) ~ /turbulence/' VFXAI_Reports/catalog_index.tsv
```

`catalog.jsonl` holds one JSON object per node with its settings (including enum options),
full slot tree with types and defaults, and — for blocks — `compatibleContexts` and
`compatibleData`, which decide where a block is allowed to live. When a value or setting name is
uncertain, read that record before writing the job. `references/job-protocol.md` has ready-made
query snippets.

## Writing a spec

A minimal system, showing the shape:

```json
{
  "path": "Assets/VFX/Sparks.vfx",
  "mode": "create",
  "graph": {
    "systems": [{
      "name": "Sparks",
      "contexts": [
        { "id": "spawn",  "node": "Spawn", "position": [0, 0],
          "blocks": [ { "node": "Constant Spawn Rate", "values": { "Rate": 120 } } ] },
        { "id": "init",   "node": "Initialize Particle", "position": [0, 220],
          "settings": { "capacity": 2048 },
          "blocks": [
            { "node": "Set Lifetime (Random Uniform)", "values": { "A": 0.6, "B": 1.6 } },
            { "node": "Set Velocity (Random Uniform)",
              "values": { "A": [-2, 1, -2], "B": [2, 5, 2] } }
          ] },
        { "id": "update", "node": "Update Particle", "position": [0, 620] },
        { "id": "output", "node": "Output Particle (Unlit) Quad", "position": [0, 950],
          "settings": { "blendMode": "Additive" } }
      ],
      "flow": [["spawn","init"], ["init","update"], ["update","output"]]
    }]
  }
}
```

`references/spec-schema.md` is the full reference: operators, exposed parameters, data links,
gradients, curves, spaces, material overrides, and every edit operation.

## Rules learned the hard way

These are not style preferences — each one came from an effect that came out wrong.

### Four ways to build an invisible effect

All four compile clean, report `"status": "ok"`, and render nothing. Check these first when an
effect does not show up, before touching size or colour.

**`Over Life` blocks belong in the Output context, not Update.** `|Multiply|_Color|Over Life`,
`|Multiply|_Size|Over Life` and friends must go in the **Output** context. Update-context
attribute writes persist frame to frame, so a Multiply composition compounds every frame:
`size *= curve(t)` and `alpha *= gradient(t)` collapse both to zero within a fraction of a second.
The tell is brutal — particle count is healthy, the graph compiles, `inspect` shows every value
correctly set, and nothing renders. Raising size or alpha changes nothing, because the compounding
eats whatever you add. In Output the block is evaluated per-frame for rendering only, so the
envelope applies non-destructively and per-particle randomness survives. Unity's `Smoke.vfx` and
the built-in templates all put them in Output; copy that layout. Initialize gets `Set Color` /
`Set Alpha` / `Set Size`, Update gets forces and drag, Output gets Orient plus every `Over Life`.

**Never leave `boundsMode` on `Automatic`.** It computes bounds on the GPU from live particles, so
with none alive yet it produces an inverted AABB (min `+inf`, max `-inf`). Unity spams
`Invalid AABB aabb` and culls the effect entirely — again, nothing renders regardless of how big
or bright the particles are. Use `"boundsMode": "Manual"` with explicit `bounds.center` and
`bounds.size` covering the effect's travel. `Recorded` is worse for generated graphs, because
nothing ever records the bounds.

**Pivot and non-uniform mesh scale do not mix.** `Set Pivot` offsets in mesh-local space and that
offset passes through the particle's scale. On a mesh at `(24, 0.03, 24)` any nonzero pivot gets
multiplied by a huge factor on some axis and lands outside the manual bounds, where it is culled —
the symptom is that the effect vanishes the moment pivot leaves zero. Pivot is safe on **quads** at
roughly uniform scale (Unity's `Flames.vfx` uses `[0, -0.39, 0]` to anchor flames at their base).
For mesh outputs, translate with a `|Set|_Position` offset in Initialize instead.

**Soft particles with no depth texture.** `useSoftParticle` needs the pipeline to resolve a depth
texture; without one the fade can drive alpha to zero. URP assets carry this per-quality —
check `m_RequireDepthTexture` on every RP asset the project ships, not just the one in the
editor. A project with a separate mobile or low-quality RP asset will typically have it off
there. See *Z-fading against scene geometry* below for the rest of it.

### Z-fading against scene geometry

Particles are flat camera-facing quads, so wherever one intersects a wall, a floor or the prop the
effect is attached to, it cuts off along a hard straight line and the illusion dies. This is the
single most common reason a decent-looking effect reads as cheap once it is placed in a real
scene. The fix is soft particles: the output fades a particle's alpha out as it approaches
whatever is already in the depth buffer, so it dissolves into the geometry instead of slicing
through it. Turn it on for anything volumetric that will sit near a surface — smoke, haze, fire,
dust, auras, ground fog.

**It is a setting plus a slot, and the order matters.** `useSoftParticle` is a boolean *setting*;
`softParticleFadeDistance` is an input *slot* that does not exist until the setting is true. This
is the canonical case of *Settings before values* — a spec that only sets the distance has it
skipped with a "no such slot" line in the log, and the effect still clips.

```json
{ "id": "output", "node": "Output Particle (Unlit) Quad",
  "settings": { "blendMode": "Alpha", "useSoftParticle": true },
  "values":   { "softParticleFadeDistance": 0.08 } }
```

Editing an existing output takes both ops in order — `setSettings` then `setValues`:

```json
{ "op": "setSettings", "target": "c3", "settings": { "useSoftParticle": true } },
{ "op": "setValues",   "target": "c3", "values": { "softParticleFadeDistance": 0.08 } }
```

Verify with `inspect`: the context's `inputs` list gains a `softParticleFadeDistance` entry. If it
is absent, the setting did not take and nothing is fading.

**The fade distance is in world units, and it is the thing people get wrong.** It is the depth
over which a particle goes from fully visible to fully gone, so it has to be scaled to the effect,
not to the room. Judge it against the size of the object the effect surrounds:

| effect scale | fade distance |
|---|---|
| small prop, hand-held item (~0.2–0.5u) | `0.02`–`0.08` |
| character-sized (~2u) | `0.1`–`0.3` |
| room-scale plume, ground fog | `0.3`–`1.0` |

Too large is the failure that does not look like a failure: the whole system dims, because every
particle is now within fade range of something. If an effect goes faint right after you fix its
clipping, this is why — lower the distance, do not raise the alpha to compensate.

**Which outputs support it.** Every `Output Particle|Unlit|*` and `Output Particle|URP Lit|*`
(Quad, Triangle, Octagon, Mesh), plus `Line` and `Point`. **Shader Graph outputs do not have it**,
and neither do strips — the only strip output registered is
`Output ParticleStrip|Shader Graph|Quad`, so a ribbon cannot be soft-particled through this
toggle at all. When something ribbon-shaped has to fade against geometry — a lightning bolt, an
arc, a beam — build it from `Output Particle|Unlit|Quad` with a bolt texture and
`Orient|Fixed Axis` or `Orient|Along Velocity` rather than a strip. Confirm what this install
actually registers rather than trusting the list:

```bash
awk -F'\t' '$1=="context" && $3 ~ /Output/' VFXAI_Reports/catalog_index.tsv | cut -f3
```

**Do not "fix" clipping with the depth-test settings.** `zTestMode: "Always"` makes the effect
draw over everything, walls included, which is a worse artifact than the one it replaces, and
`zWriteMode: "On"` on a transparent output makes particles cull each other. Soft particles fade;
the z settings decide occlusion. They are different problems and only the first one is about
intersection.

**Placement does half the work.** For an aura around an object, emit on a thin shell just outside
the surface (`positionMode` `Surface` or `ThicknessAbsolute` on a shape slightly larger than the
mesh) rather than a volume centred on it. Volume emission starts every particle *inside* the
model, where it has to punch out through the surface and soft particles have nothing to save. A
`Collision Shape|Sphere` (Solid, Bounce 0) in Update keeps turbulence from pushing them back in.

### Everything else

**Put alpha in the Color parameter, not a separate float.** `Set Color`'s slot is Vector3 because
VFX Graph stores `color` and `alpha` as separate attributes, so linking a `color` parameter to it
silently drops the alpha channel. Exposing a second float alongside leaves a dead alpha slider in
the Inspector that looks live and does nothing. Split the one Color with a `Swizzle`:

```json
"parameters": [ { "id": "pCol", "name": "HazeColor", "type": "color",
                  "value": [0.10, 0.02, 0.18, 0.70], "exposed": true } ],
"operators":  [ { "id": "swz", "node": "Swizzle", "settings": { "mask": "w" } } ],
"links": [
  { "from": "pCol", "to": "setColor", "toSlot": "_Color" },
  { "from": "pCol", "to": "swz",      "toSlot": "x" },
  { "from": "swz",  "to": "setAlpha", "toSlot": "_Alpha" }
]
```

`mask: "w"` reports as `Swizzle.w (float)` and links straight into `_Alpha`; the Color → Vector3
conversion into `_Color` is implicit.

**Every particle is a circle until you give it a texture.** Outputs default to `DefaultDot`, a
soft radial dot, so an untextured graph can only vary size, colour and alpha — it reads as flat
and primitive no matter how it is tuned, and no amount of colour work fixes a silhouette problem.
This skill ships a CC0 library for exactly this reason: 386 shapes, flipbook sheets and frame
sequences in `assets/Textures/`. Run a `textures` job first — it reports what the project already
has, and it is the only way to know whether a file's shape lives in its alpha channel or its RGB,
which decides the blend mode. Install the bundle only if that scan finds nothing usable.
`references/textures.md` covers the library, the encodings and the import settings.

The bundle is a **source archive**, not an asset folder. It lives outside `Assets/`, so Unity
never imports it and nothing in it has a GUID. Installing means copying the folders into
`<project>/Assets/Textures/` and letting Unity import them; from then on every path you write —
`mainTexture` values, `texconfig` folders, `flipbook` frames — is the imported
`Assets/Textures/…` path. A path into the skill's own folder is not an asset path: the value is
skipped, the output silently falls back to `DefaultDot`, and in a fresh clone that folder may be
gitignored and absent entirely.

Ranked fixes: flipbooks (`wispy_smoke_03_8x8`, `flame_01_16x4`) > a Shader Graph output with
noise-eroded alpha > shaped textures (`spark_04_a`, `magic_02_a`, …) > texture-free silhouette
breaking (non-uniform `Set Scale`, random `Angle` + `Angular Velocity`, layered systems). Note
that soft bar textures like `trace_01_a` stretched on a non-uniformly scaled quad read as exactly
that — a stretched texture — not as a wisp.

**An opaque-alpha texture on an Alpha-blended output is a black square.** Half the shapes in the
library — everything under `particles/opague/`, plus the 24-bit `fire_0*_8x8` sheets — carry the
shape as greyscale RGB with alpha solid at 255. They are built for `Additive`, where black adds
nothing. On `blendMode: "Alpha"` the transparent background is not transparent and every particle
renders as its full bounding square. The `textures` op reports this as `hasAlpha: false`; the
`_a` variants under `particles/alpha/` are the safe default, white RGB with the shape in alpha,
so `Set Color` tints them properly.

**Solid shapes need mesh outputs, not textures.** For rays, cones, rings and beams use
`Output Particle|Unlit|Mesh`: real geometry with a real silhouette that turns correctly in 3D.
Set `cullMode: "Off"` so thin geometry stays visible from inside and `zWriteMode: "Off"` so
instances layer additively instead of z-fighting. Pin `|Set|_Size` to `1.0` so `|Set|_Scale` is
the sole driver and the numbers mean what they say. The Learning Templates sample ships
`ST_FXBase_Cone.fbx` and `ST_Tube.fbx` for exactly this. Expose the mesh as a `"type": "mesh"`
parameter so shapes can be swapped without a rebuild.

**Calibrate an unknown mesh with a multiplier, not a magic number.** Sample meshes are not authored
to a common scale — `ST_Tube` is a thin Y-aligned pipe whose X/Z needs roughly **120×** to become a
visible hoop, while its Y reads in world units already. Keep the exposed parameter in world units
and put a `Multiply` operator between it and the scale slot, exposing the factor separately
(`RingRadius` 0.20 × `RadiusToMesh` 120 = 24). Swapping the mesh then means recalibrating one
number rather than relearning what "24" meant.

**A link overrides a slot's literal.** Once a parameter or operator drives a slot, `setValues` on
that slot silently does nothing. To change a parameter-driven value, change the parameter, or
rebuild with `replace`.

**Prefer `Cone / Cylinder` over `Circle` for horizontal rings.** Its axis is reliably Y-aligned,
where the circle shape's default plane is not. A near-zero `height` (0.02) with equal base and top
radius gives a flat ring; `baseRadius` > `topRadius` gives a tapering column.

**Set `blendMode` on every output that should glow.** It defaults to `Alpha`, which turns sparks
and embers into flat grey discs. Additive is almost always what someone means by fire, sparks,
magic or energy. Options: `Additive`, `Alpha`, `AlphaPremultiplied`, `Opaque`.

**Never put a randomizing position block on a strip system.** Strips connect particles in spawn
order into a ribbon, so a random position per particle makes the ribbon zigzag between random
points. Strip particles should be born at one place and then *move*.

**Keep spawn rate at or below the frame rate for anything that follows a moving transform.**
A particle's spawn position is evaluated once per frame, so at 160/s on a 60fps editor roughly
three particles per frame are born at the *exact same point*. In a strip that means degenerate
zero-length segments and hard-edged clumps; in a quad trail it means beads spaced one frame
apart. Unity's own trail sample runs at **24/s**. Get length from lifetime, not from rate — that
adds particles without duplicating positions. The tell is that the artifact worsens when the
editor lags, because longer frames stack more particles per position.

**Fade alpha in from zero at birth on trails.** A particle born at full opacity is a hard-edged
patch that then travels down the ribbon. Combined with per-frame clustering it reads as squares
popping along the trail. Ramping alpha from 0 to full over the first ~15% of life makes a clump
fade in over several frames instead of appearing instantly.

**Strips need `sort: Off`.** Strip geometry threads through particles in index order; depth
sorting reorders them, so the ribbon connects particles that are not neighbours and throws quads
across the gaps. Every strip output in Unity's samples has sorting off.

**Ribbons overlap at sharp bends and that is not fixable.** Where the turn radius approaches the
ribbon's half-width, the inside of the curve folds through itself. Unity's own samples show it.
Mitigate with a narrower ribbon, gentler paths, or non-additive blending so overlaps do not blow
out to white — but do not promise it away.

**A trail behind a moving object needs World space.** Set the Initialize context's
`"space": "World"`, then give the position slot `{"space": "Local", "value": [0,0,0]}`. VFX Graph
converts the local origin into the emitter's current world position, so each particle is born
where the object is right now and then stays there. Left in Local space, the ribbon rides along
with the transform and trails nothing. A trail also needs something moving — use a `scene` job
with a `mover`, or the effect is impossible to judge standing still.

**Orient blocks DO work on strips — `compatibleData` is a bitmask, not a value.**
`ParticleStrip = 1 << 4 | Particle`, and compatibility is `(from & to) != 0`, so anything marked
`Particle` accepts strip data too. Unity's `Simple_Trail` puts `Orient: Face Camera Position` in
its strip output. Read those flags as flags; a ribbon can absolutely face the camera.

**Blend mode on Shader Graph outputs is not a setting.** Those outputs have no `blendMode`; their
blending lives in a material property map. Use `"material": {"_Blend": 2}` on the context, or a
`setMaterial` edit. Run `inspect` first — it lists every float property the shader exposes. The
builder calls the SRP binder's `SetupMaterial` afterwards, which is what actually applies blend
state and keywords; writing the floats alone changes nothing about how the material renders.

**Read Unity's own graphs before inventing structure.** The built-in templates and the Learning
Templates sample are the ground truth for what is idiomatic, and `inspect` reads their sticky
notes, group boxes and context labels — the sample notes explain design intent in prose. Almost
every mistake worth making here produces a graph that compiles perfectly and looks wrong, so
comparing against a working reference beats reasoning from the data model. When an effect
misbehaves, place Unity's equivalent sample in the scene alongside it: if the sample shows the
same artifact, it is inherent and not worth chasing.

**Composite slots take nested objects or arrays.** A `Vector`, `Position` or `Sphere` slot is a
tree, not a value. Arrays work where the shape is unambiguous (`"Force": [0,-6,0]`), otherwise
address children (`{"arcSphere": {"sphere": {"radius": 1.5}}}`) or name the leaf directly
(`"radius": 1.5`). Leading underscores in slot names are optional: `"Lifetime"` finds `_Lifetime`.

**Settings before values.** Some settings change which slots exist — switching a Set block's
`Composition` to `Blend` adds a `Blend` slot, and `Random` to `Uniform` replaces one input with
`A`/`B`. The builder applies settings first for this reason; when editing, do the same.

## Judging the result

You cannot see the effect. Do not claim it looks good. Say what you built and what you expect it
to look like, ask the person to look, and take their description seriously — "it's just circles"
meant a blend mode was wrong, "zigzagging in place" meant a position block did not belong. Visual
feedback is the only signal that the effect is actually right, so ask for it plainly.

## References

- `references/install.md` — installing the bridge into a Unity project, requirements, recovery
- `references/job-protocol.md` — ops, job files, results, approval, catalog queries
- `references/spec-schema.md` — the full spec and edit-operation reference
- `references/textures.md` — the bundled library, channel encodings, import settings, flipbooks
- `references/recipes.md` — worked examples: burst, embers with GPU events, world-space trail
