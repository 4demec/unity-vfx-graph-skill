# Installing the bridge

## Pre-flight, before installing anything

Read `<project>/ProjectSettings/ProjectVersion.txt` for the editor version and
`<project>/Packages/manifest.json` for the render pipeline and whether VFX Graph is present.
Both exist before the bridge does, so this is how you check eligibility up front rather than
discovering a problem after installing. After the bridge runs, `VFXAI_Reports/editor_status.json`
and `kernel_status.json` report the same things plus the active SRP binder.

## Requirements

- **Unity 6.x.** Developed and verified against 6000.5.9f1 / VFX Graph 17.5.0, and used on
  6000.4.x / 17.4.0. The kernel compiles against `UnityEditor.VFX` internals, which carry no
  compatibility guarantee, so treat any other version as unverified until a `catalog` job
  succeeds — that is the cheap test that the internals still line up.
- **URP or HDRP.** VFX Graph does not support the built-in render pipeline. It is fully out of
  preview on HDRP; on URP it is still flagged preview and some outputs (HDRP lit decals,
  volumetric fog) simply do not exist. Check `kernel_status.json` for `srpBinder` after the
  first catalog run to see which pipeline is active.
- **The `com.unity.visualeffectgraph` package.** If `Packages/manifest.json` has no
  `com.unity.visualeffectgraph` entry, add one. It is a core package, so its version tracks
  the editor and is not a free choice — never copy a version number out of this document.
  Read the version this project already resolved and match it exactly:

  ```bash
  grep -A1 'render-pipelines' "$P/Packages/manifest.json"   # core packages share a version line
  ```

  ```json
  "com.unity.visualeffectgraph": "<the same version as the other core packages>",
  ```

  Unity resolves it from the local offline cache; no download needed.
- Compute shader and SSBO support. No OpenGL ES. URP additionally requires linear colour space.

## Layout

Copy `assets/VFXAI/` to `<project>/Assets/VFXAI/`. The four folders are deliberately separate:

```
Assets/VFXAI/
├── Kernel/    VFXAI.Kernel.asmref  → compiles INTO Unity.VisualEffectGraph.Editor
│              VfxAiBuilder.cs, VfxAiCatalog.cs, VfxAiKernelApi.cs, VfxAiJson.cs,
│              VfxAiTextures.cs
├── Bridge/    VFXAI.Bridge.asmdef  → references nothing; job queue + approval window
├── Status/    VFXAI.Status.asmdef  → references nothing; heartbeat + compile-error watchdog
└── Runtime/   VfxAiPreviewMover.cs → plain runtime script, moves preview objects
```

**Why the split matters.** The kernel must live inside Unity's own VFX editor assembly, because
every authoring type is `internal` — an `.asmref` file is the supported way to compile your
source into an existing assembly. The cost is that a compile error in the kernel breaks VFX Graph
itself and everything downstream of it. So the bridge and the watchdog reference *nothing* and
reach the kernel by reflection: if the kernel stops compiling, the job queue still runs and can
still process a `refresh` job to pick up the fix, and the watchdog still writes the errors to a
file. Keep that isolation if you modify the tooling.

`VfxAiTextures.cs` needs no VFX internals — it is plain UnityEditor API — but it lives in the
kernel so it can share the JSON helpers and the single `Invoke` entry point. It carries the
`textures`, `texconfig` and `flipbook` ops.

## The texture library (separate, optional)

`assets/Textures/` in this skill is a CC0 particle library — 386 shapes, flipbook sheets and
frame sequences, ~82 MB. It is **not** part of the bridge and is not needed to author graphs;
install it only when the project has no usable particle textures, which a `textures` job tells
you. Copy the two folders into `<project>/Assets/Textures/` keeping their names
(`brackeys_vfx_bundle`, `kenneynl`), plus `LICENSE & CREDITS.txt`, then refresh and run the
import presets. `references/textures.md` has the commands and the reasoning.

No `.meta` files ship with the library on purpose: Unity generates fresh ones on import, so there
are no GUID collisions with anything the project already has.

## First run

Unity must import the files once. Focus the Unity window, or trigger a refresh from the
editor (Ctrl+R on Windows, Cmd+R on macOS). After that, `refresh` jobs handle recompiles by
themselves.

Confirm with a ping (see SKILL.md). Then run a `catalog` job to produce the node index.

## Recompiling after you change the tooling

Write the changed `.cs` file, then queue a refresh:

```bash
printf '{}' > "<project>/VFXAI_Jobs/900.refresh.job"
```

This forces a recursive synchronous import of `Assets/VFXAI` and then calls
`CompilationPipeline.RequestScriptCompilation()`. Both halves are needed: a bare
`AssetDatabase.Refresh()` is deferred while Unity sits in the background and never scans the
folder, so new files are not even noticed. With the forced import, a recompile lands in a few
seconds with Unity unfocused.

Verify it took effect rather than assuming — compare `Library/ScriptAssemblies/*.dll` timestamps,
check `VFXAI_Reports/compile_log.json`, and run a `version` job to see the live kernel version.

## If the kernel will not compile

`VFXAI_Reports/compile_log.json` lists file, line and message. Fix the source, queue a refresh.
The watchdog assembly has no dependencies, so it keeps reporting even when everything else is
broken.

If Unity is wedged badly enough that the job queue is not running either, move
`Assets/VFXAI/Kernel/` out of `Assets/` (for example to `<project>/_disabled/`) and let Unity
recompile without it — VFX Graph comes back, then put the fixed files back.

## Files the tooling writes

All outside `Assets/`, so they never trigger reimports:

- `VFXAI_Jobs/` — job files you write; `processed/` keeps what has run
- `VFXAI_Results/` — one result JSON per job
- `VFXAI_Reports/` — catalog, heartbeat, editor status, compile log

Add them to `.gitignore` if the project is under version control.
