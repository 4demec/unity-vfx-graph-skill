# Textures

An untextured output is a `DefaultDot` — a soft radial blob. No amount of colour, size or curve
work fixes a silhouette problem, so picking a texture is not decoration, it is the first
decision. This skill ships a library so there is always something to pick.

## The bundled library

`assets/Textures/` in this skill holds 386 CC0 images (~82 MB). It is a **source archive, not an
asset folder**: it sits outside `Assets/`, so Unity never imports it, there is no GUID for
anything in it, and a `.vfx` cannot reference it. Its only job is to seed a project that has no
particle textures.

> **Always reference `Assets/Textures/…` in a spec, never the skill's own copy.** Every
> `mainTexture` value, every `texconfig` folder, every `flipbook` frame path is a project-relative
> `Assets/` path. A path pointing into `.claude/skills/…` is not an asset path — the value is
> silently skipped and the output falls back to `DefaultDot`. It is also usually gitignored, so
> it may not even exist in a fresh clone.

### Installing it

**1. Check what the project already has** — never copy 82 MB over an existing library:

```bash
cat > "$P/VFXAI_Jobs/010.textures.job" <<'EOF'
{ "folder": "Assets", "limit": 500 }
EOF
```

If the scan already lists `Assets/Textures/brackeys_vfx_bundle/…` or `Assets/Textures/kenneynl/…`,
the library is installed — skip to *Import settings*. A project may also carry its own particle
textures under some other name; a scan with no filter finds those too, and they are usually the
better choice because they match the project's art.

**2. Copy the folders in**, keeping their names, if and only if step 1 found nothing usable:

```bash
cp -r "<skill>/assets/Textures/brackeys_vfx_bundle" "<project>/Assets/Textures/"
cp -r "<skill>/assets/Textures/kenneynl"            "<project>/Assets/Textures/"
printf '{}' > "$P/VFXAI_Jobs/011.refresh.job"        # let Unity import them
```

Copying into `Assets/` is what makes them real assets: Unity imports them, writes a `.meta` with a
fresh GUID for each, and only then can a graph point at them. No `.meta` ships with the bundle, so
there is nothing to collide with. Keep `LICENSE & CREDITS.txt` next to the images — everything is
CC0, crediting Kenney, Picster, Thomas Iché (flipbooks) and CodeManu (pre-drawn sheets).

**3. Fix the import settings.** They arrive on Unity's defaults, which are wrong for particles in
three ways (see *Import settings* below):

```bash
cat > "$P/VFXAI_Jobs/012.texconfig.job" <<'EOF'
{ "folder": "Assets/Textures", "preset": "particle" }
EOF
cat > "$P/VFXAI_Jobs/013.texconfig.job" <<'EOF'
{ "folder": "Assets/Textures/brackeys_vfx_bundle/flipbooks", "preset": "flipbook" }
EOF
cat > "$P/VFXAI_Jobs/014.texconfig.job" <<'EOF'
{ "folder": "Assets/Textures/brackeys_vfx_bundle/predrawn", "preset": "flipbook" }
EOF
```

**Every folder holding sheets needs the `flipbook` preset, not just the one called `flipbooks`.**
`predrawn/` is sheets too, and it is the folder where it matters most: those 14 are all
non-power-of-two, so on the default `npotScale: ToNearest` Unity rescales each sheet at import and
every frame's aspect shifts. The `particle` preset does not touch `npotScale` or mipmaps, so a
sweep over `Assets/Textures` leaves them wrong while reporting success.

`texconfig` modifies assets, so both wait for approval in the control panel. Say so before
queueing them.

**4. Re-scan** and use the paths the scan reports, verbatim. That is the list of textures that
exist as far as Unity is concerned.

## Three encodings, and why it decides your blend mode

Same-looking PNGs, different channel layouts. Getting this wrong is the "why is my particle a
black square" bug:

| set | RGB | alpha | use with |
|---|---|---|---|
| `brackeys_vfx_bundle/particles/alpha/*_a.png` | pure white | the shape | **anything** — tint freely with `Set Color`, works on `Alpha` and `Additive` |
| `brackeys_vfx_bundle/particles/opague/*.png` | the shape, greyscale | **opaque everywhere** | **`Additive` only.** On `Alpha` it renders the full black square |
| `kenneynl/General/*.png` | the shape, greyscale | the shape | either, but the greyscale darkens any tint you apply |
| `brackeys_vfx_bundle/flipbooks/fire_0[1-4]_8x8.tga` | the shape | **no alpha channel at all** (24-bit) | **`Additive` only** |

Default to the `alpha/` set. White-RGB-plus-alpha is the only encoding where `Set Color` means
what it says, including HDR values above 1.0 for bloom.

Never assume from the file name — run a `textures` job and read `hasAlpha`. It reports the source
channel layout, and flags exactly these cases in `issues`.

## What is in the library

**`brackeys_vfx_bundle/particles/alpha/`** — 93 shapes, 512×512, the everyday set:

| family | count | reads as |
|---|---|---|
| `circle_01…05` | 5 | soft cores, glows, generic dots — the least interesting choice |
| `light_01…03`, `flare_01` | 4 | lens glow, bloom seeds, highlight pops |
| `smoke_01…10` (`smoke_07_strong`) | 11 | puffs and billows; the workhorse for anything soft |
| `fire_01…02`, `flame_01…06` | 8 | flame licks with a torn top edge |
| `spark_01…07` | 7 | hot points and short streaks |
| `trace_01…07` | 7 | soft bars — trails, strips, speed streaks |
| `star_01…09` | 9 | four- and six-point sparkles, pickups, magic |
| `magic_01…05`, `symbol_01…02` | 7 | runes, arcane rings, glyphs |
| `muzzle_01…05` | 5 | radial flashes, impact pops |
| `twirl_01…04` | 4 | swirls and vortices |
| `slash_01…04`, `scratch_01` | 5 | arcs, cuts, claw marks |
| `spotlight_01…08` | 8 | cones and beam gradients |
| `window_01…04` | 4 | rings and frames — shockwave outlines |
| `dirt_01…03` | 3 | grit clusters, debris |
| `scorch_01…03` | 3 | soot decals, ground marks |
| `effect_01…03` | 3 | abstract blooms |

`particles/opague/` mirrors these (92 files, same names without `_a`) in the Additive-only
encoding. `kenneynl/General/` is the same shape set again from the upstream Kenney pack;
`kenneynl/General/Rotated/` is the genuinely useful part — `trace_*`, `muzzle_*`, `spark_05/06`,
`flame_05/06` rotated 90°, for quads stretched along Y rather than X.

**`brackeys_vfx_bundle/flipbooks/`** — 14 animated sheets, TGA. Grid is in the file name:

| sheet | grid | size | alpha | reads as |
|---|---|---|---|---|
| `wispy_smoke_01…03_8x8` | 8×8 | 1024² | yes | thin drifting smoke — the reference for soft |
| `cloud_01…02_8x8` | 8×8 | 1024² | yes | thick billowing puffs |
| `explosion_01…02_8x8` | 8×8 | 1024² | yes | fireball bloom |
| `explosion_smoke_01_8x8` | 8×8 | 1024² | yes | the dirty aftermath |
| `fire_01…04_8x8` | 8×8 | 1024² | **no** | looping fire, Additive only |
| `flame_01_16x4` | 16×4 | 2048×1024 | yes | 64 frames of a single flame lick |
| `flame_02_15x4` | 15×4 | 2048×1024 | yes | 60 frames; 2048/15 is not integer, so frame edges land mid-texel — harmless in practice, but prefer `flame_01` when either would do |

**`brackeys_vfx_bundle/predrawn/`** — 14 pre-coloured sheets (`big_hit_6x5`, `blood_impact_6x5`,
`charge_7x6`, `dithered_fire_6x5`, `electric_ring_6x5`, `explosion_6x5`, `fire_point_6x5`,
`fire_ring_6x5`, `impact_white_6x4`, `lightstreaks_6x5`, `star_explosion_6x5`, `vortex_6x5`,
`wavy_blue_6x5`, `wavy_purple_6x5`). Stylised and already coloured — tinting fights the art, so
use them at `Set Color` white. All are non-power-of-two (e.g. 2130×1775), which matters: with
Unity's default `npotScale: ToNearest` the sheet is rescaled on import and every frame's aspect
shifts. The `flipbook` preset sets `npotScale: None`.

Two of them do not divide evenly into their own grid — `impact_white_6x4` (1746×1505, 1505/4) and
`star_explosion_6x5` (840×654, 654/5) — so frame edges land mid-texel. With `wrapMode: Clamp` and
mipmaps off it does not show; there is nothing to fix short of re-cropping the art. `flame_02_15x4`
in the flipbooks folder has the same property (2048/15).

**`kenneynl/` sequences** — `Black smoke` (25), `White puff` (25), `Explosion` (9), `Flash` (9),
`Fart` (9) are **loose frames, not sheets**, and each frame is cropped to its own bounds
(362×336, 368×407, …). VFX Graph cannot play those; assemble a sheet first with the `flipbook`
op, which centres each frame in a uniform cell.

## Import settings

The packs import on Unity's defaults, which are wrong for particles three times over. A
`textures` job reports each one in `issues`:

- **`alphaIsTransparency` off.** Colour in fully transparent pixels is undefined, so bilinear
  filtering drags it into the soft edge — dark fringes around every particle.
- **Wrap mode `Repeat`.** Edge texels wrap to the opposite side. On a flipbook that means the
  last column bleeding into the first.
- **Mipmaps on a flipbook sheet.** Lower mips average across frame boundaries, so a distant
  effect blends every frame into a smear. Off for sheets, on for single sprites (a 512² dot with
  no mips aliases badly at distance).

The two presets:

| preset | sets |
|---|---|
| `particle` | `textureType: Default`, `alphaIsTransparency: true`, `alphaSource: FromInput`, `wrapMode: Clamp` |
| `flipbook` | the same, plus `mipmaps: false`, `npotScale: None` |

Anything else goes in `settings`, which is applied after the preset:
`alphaIsTransparency`, `mipmaps`, `sRGB`, `readable`, `maxTextureSize`, `wrapMode`, `filterMode`,
`npotScale`, `compression`, `alphaSource`, `textureType`.

512² for a spark that renders 8 px wide is pure memory. Once an effect is settled, drop the ones
that stay small:

```json
{ "folder": "Assets/Textures/brackeys_vfx_bundle/particles/alpha", "filter": "spark",
  "settings": { "maxTextureSize": 128 } }
```

`"dryRun": true` reports the same per-file `field: old -> new` diff without touching anything —
worth running first on a wide folder, since a full `Assets/Textures` sweep reimports hundreds of
files.

`"force": true` writes every matched file even when the importer reports it is already correct.
Reach for it when a job says `unchangedCount` but the `.meta` on disk disagrees — a `.meta` edited
behind Unity's back, or an importer left dirty in memory. Verify against the file, not the job:
`grep -E "enableMipMap|alphaIsTransparency|nPOTScale|wrapU:" <asset>.meta`. This is the texture
version of the skill's standing rule that a successful log is not evidence the value landed.

## Using one in a spec

`mainTexture` is an input **slot** on `Output Particle|Unlit|Quad` / `Octagon` / `Mesh` — it goes
in `values`, addressed by asset path. Shader Graph outputs use their own property name (often
`_BaseMap`); run `inspect` rather than guessing.

Single sprite:

```json
{ "id": "output", "node": "Output Particle (Unlit) Quad",
  "settings": { "blendMode": "Additive" },
  "values": { "mainTexture": "Assets/Textures/brackeys_vfx_bundle/particles/alpha/spark_04_a.png" },
  "blocks": [ { "node": "Orient (Along Velocity)" } ] }
```

Flipbook. `flipBookSize` **does not exist as a slot until `uvMode` is `Flipbook`** — the builder
applies settings before values, so one context object is fine, but when editing an existing graph
set `uvMode` first and the size second:

```json
{ "id": "output", "node": "Output Particle (Unlit) Octagon",
  "settings": { "blendMode": "Alpha", "uvMode": "Flipbook", "flipbookLayout": "Texture2D",
                "flipbookBlendFrames": true, "cropFactor": 0.293 },
  "values": { "mainTexture": "Assets/Textures/brackeys_vfx_bundle/flipbooks/wispy_smoke_03_8x8.tga",
              "flipBookSize": [8, 8] },
  "blocks": [ { "node": "Orient (Face Camera Plane)" } ] }
```

Then in Initialize `|Set|_Tex Index|Random Uniform` with `A: 0`, `B: 63` so particles do not
animate in lockstep, and in Update `Flipbook Player` with `mode: "FrameRate"` — 8–14 fps for slow
ooze, 20–32 for energy. Cap `B` at `columns × rows − 1`.

`cropFactor` on the Octagon output trims the transparent border; 0.293 is Unity's value for the
8×8 smoke sheets and cuts real overdraw on a texture that is mostly empty.

## Assembling a sheet from loose frames

```json
{ "folder": "Assets/Textures/kenneynl/Black smoke",
  "output": "Assets/Textures/generated/blackSmoke.png",
  "columns": 5, "rows": 5, "cellSize": 256 }
```

- Frames come from `folder` (+ optional `filter`) sorted by name, or from `frames` as an explicit
  ordered list. PNG and JPG only — it reads the source file, not the imported texture, so
  nothing has to be marked readable and TGA is not supported.
- The grid defaults to the nearest square; `cellSize` defaults to the next power of two above the
  largest frame, shrinking to keep the sheet within 4096.
- Frames are centred in their cell preserving aspect (`"fit": "stretch"` to fill instead), laid
  out **left to right, top to bottom** the way Unity reads flipbooks, on transparent black so
  Additive stays clean.
- The grid is appended to the output name — `blackSmoke.png` lands as `blackSmoke_5x5.png` — so a
  later `textures` scan can read the layout back out of the file name. The result reports the
  exact `flipBookSize` to use.
- The new sheet is imported with the `flipbook` preset already applied.

Partial grids leave empty cells at the end; a Flipbook Player walking the full grid then flashes
blank frames, so cap `Tex Index` at the real frame count instead.

## Picking guide

| you want | reach for |
|---|---|
| smoke, steam, dust cloud | `wispy_smoke_0*_8x8` flipbook; `smoke_0*_a` if a sheet is overkill |
| fire, torch, burning | `fire_0*_8x8` (Additive) or `flame_01_16x4`; `flame_0*_a` for cheap licks |
| sparks, embers | `spark_0*_a`, stretched along velocity, Additive |
| explosion | `explosion_01_8x8` + `explosion_smoke_01_8x8` layered, or `predrawn/explosion_6x5` |
| magic, arcane, buff | `magic_0*_a`, `star_0*_a`, `symbol_0*_a`, HDR tint, Additive |
| impact, hit flash | `muzzle_0*_a`, `predrawn/big_hit_6x5`, `predrawn/impact_white_6x4` |
| shockwave ring | `window_0*_a` on a quad, or a mesh ring (see SKILL.md — solid shapes want geometry) |
| trail, ribbon, streak | `trace_0*_a` (or the `Rotated` variants if the quad stretches on Y) |
| debris, grit, dirt | `dirt_0*_a` |
| beams, god rays, cones | `spotlight_0*_a`, though a mesh output reads better in 3D |
| toon poof | `kenneynl/White puff` or `Black smoke`, assembled into a sheet first |

Layering two systems with different textures — a sheet for the body, sparks over it — is what
separates a finished effect from a single emitter. Cheap to add, and it is usually the difference
the person is asking for when they say it looks flat.
