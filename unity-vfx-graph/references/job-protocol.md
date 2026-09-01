# Job protocol

## How a job runs

Write a file into `<project>/VFXAI_Jobs/` named `<anything>.<op>.job`. The op comes from the
filename; the file contents are the JSON arguments (`{}` if none). The bridge polls once a
second off `EditorApplication.update`, which keeps ticking while Unity is unfocused — so jobs run
whether or not anyone is looking at the editor.

The answer appears at `<project>/VFXAI_Results/<basename>.result.json` and the job file moves to
`VFXAI_Jobs/processed/`. Number the prefixes (`010.apply.job`) so ordering is predictable.

```bash
cat > "$P/VFXAI_Jobs/010.apply.job" <<'EOF'
{ "path": "Assets/VFX/Sparks.vfx", "mode": "create", "graph": { ... } }
EOF
sleep 5
cat "$P/VFXAI_Results/010.apply.result.json"
```

Result envelope:

```json
{
  "job": "010.apply.job",
  "op": "apply",
  "startedUtc": "...",
  "finishedUtc": "...",
  "result": { "status": "ok", "log": [ ... ], "graphErrors": [ ... ] }
}
```

`status` is `ok`, `error`, `pending` or `rejected`. **Always read `log`.** A job reports `ok`
when the asset compiled, even if individual values were skipped — every skip is a line in the
log, and it names the valid alternatives.

## Approval

`apply` and `scene` modify the project, so they queue in **Tools > VFX AI > Control Panel** and
report `pending` until a human clicks Approve. Re-read the result file after approval. Read-only
ops (`ping`, `version`, `catalog`, `list`, `inspect`, `refresh`) run immediately.

Auto-approve exists as a toggle in the panel, but it is the human's switch to flip, not
something to work around. Say what a job will do before queueing it.

Jobs against the same asset are serialised: the panel disables Approve and Reject on any job
whose target already has an older job pending. Edits address nodes by ids captured from one
specific graph state, so approving out of order would silently apply them to different nodes.
Queue dependent jobs in the order they must run, and expect the human to work through them
oldest first.

## Ops

| op | modifies | purpose |
|---|---|---|
| `ping` | no | is the bridge alive, is the kernel loaded |
| `version` | no | kernel and API versions |
| `refresh` | no | force import + script recompilation (after changing tooling C#) |
| `catalog` | no | dump the node library of this install (args `{}`; takes a few seconds) |
| `list` | no | every VisualEffectAsset in the project |
| `inspect` | no | read a `.vfx` back as addressable nodes, values, links |
| `apply` | **yes** | create, replace or edit a `.vfx` |
| `scene` | **yes** | place the effect on a GameObject, optionally with a mover |
| `textures` | no | scan Texture2D assets: size, alpha, flipbook grid, import problems |
| `texconfig` | **yes** | apply texture import settings (presets `particle` / `flipbook`) |
| `flipbook` | **yes** | assemble loose PNG frames into one flipbook sheet |

### catalog

Takes `{}`. Writes four files into `VFXAI_Reports/`, all of them regenerated each run:

| file | contents |
|---|---|
| `catalog_index.tsv` | `kind, category, name, modelType, variantOf` — grep this first |
| `catalog.jsonl` | one JSON object per node: settings with enum options, full slot tree, compatibility |
| `slot_types.tsv` | property types usable for exposed parameters and slot values |
| `kernel_status.json` | kernel/Unity versions, node counts, active SRP binder |

A few seconds on a normal project. Re-run it after a Unity upgrade or if the index is missing.

### Waiting for results

Read-only ops normally land within a couple of seconds; poll rather than guessing a sleep:

```bash
for i in $(seq 1 20); do
  [ -f "$P/VFXAI_Results/010.apply.result.json" ] && break
  sleep 1
done
cat "$P/VFXAI_Results/010.apply.result.json"
```

A result file appears almost immediately for approval-gated ops too — but with
`"status": "pending"`. That is not the answer, it means the job is sitting in the control panel.
Re-read the same file after the human approves; the file is overwritten in place. There is no
notification, so say what needs approving and check back rather than polling in a tight loop.

### inspect

```json
{ "path": "Assets/VFX/Sparks.vfx" }
```

Returns nodes with stable-for-now ids — `c0` contexts, `c0.b1` blocks within them, `o0`
operators, `p0` parameters — plus each node's settings, space, slot values, incoming links, and
for rendered outputs the material float properties. It also returns `stickyNotes` (title, full
text, and the nearest node), `groups`, and any `userLabel` typed on a context. On Unity's Learning
Templates those notes carry the actual explanation of the technique, which is usually worth more
than the node data — inspect a sample before building anything unfamiliar. These ids are what `edit` mode addresses, so
inspect immediately before editing; restructuring the graph renumbers them.

### textures, texconfig, flipbook

The texture side of the tooling. `references/textures.md` is the reference — what the bundled
library holds, which channel encoding forces which blend mode, and what the import presets fix.
In short:

```json
{ "folder": "Assets/Textures", "filter": "smoke", "limit": 400, "report": true }
```

`textures` is read-only. It walks `t:Texture2D` under `folder` (or `folders`, default `Assets`),
and reports each one's source size, whether the source actually has an alpha channel, the
flipbook grid parsed from a trailing `_8x8` in the name, the importer flags that matter, and an
`issues` list naming what will go wrong. It also writes `VFXAI_Reports/texture_index.tsv`:

```bash
# every sheet whose frames will smear at distance
awk -F'\t' '$4!="" && $8=="1" {print $1}' $R/texture_index.tsv

# shapes that only work on Additive
awk -F'\t' '$6=="0" {print $1}' $R/texture_index.tsv
```

`texconfig` takes the same `folder`/`filter` selection or an explicit `paths` array, plus a
`preset` (`particle`, `flipbook`) and/or a `settings` object, and reimports what changed.
`"dryRun": true` reports the `field: old -> new` diff without writing; `"force": true` writes
every matched file even when the importer claims it is already correct. Confirm a run by reading
the `.meta`, not the `changedCount` — an importer whose in-memory state has drifted from disk
reports `unchanged` and writes nothing. `flipbook` takes a frame
folder or an ordered `frames` array and writes one sheet, named with its grid.

### scene

```json
{
  "asset": "Assets/VFX/Trail.vfx",
  "name": "Trail Preview",
  "position": [0, 1, 0],
  "mover": { "motion": "Orbit", "radius": 3.0, "speed": 1.2, "height": 1.0 },
  "select": true,
  "openGraph": false,
  "save": false
}
```

Reuses a GameObject of that name if it exists. `mover` attaches `VfxAiPreviewMover`, which runs
under `[ExecuteAlways]` so the object moves in the Scene view without entering play mode —
essential for trails and anything else that only makes sense in motion. Motions: `Orbit`,
`Figure8`, `PingPong`, `Spiral`. Omit `mover` for a stationary effect. `save` writes the scene to
disk; leave it false unless asked.

## Querying the catalog

`catalog_index.tsv` is `kind, category, name, modelType, variantOf`. Grep it before writing specs.

```bash
R=<project>/VFXAI_Reports

# all contexts
awk -F'\t' '$1=="context"' $R/catalog_index.tsv | cut -f2,3 | sort -u

# blocks matching a word
awk -F'\t' '$1=="block" && tolower($3) ~ /gravity|drag|turbulence/' $R/catalog_index.tsv | cut -f2,3

# operators by category
awk -F'\t' '$1=="operator" && $2 ~ /Math/' $R/catalog_index.tsv | cut -f2,3 | head -40
```

For settings, enum options, slot paths and compatibility, read the full record from
`catalog.jsonl`:

```bash
python3 - <<'PY'
import json
want = {"Output Particle|Unlit|Quad", "Turbulence"}
for line in open("VFXAI_Reports/catalog.jsonl", encoding="utf-8"):
    d = json.loads(line)
    if d.get("name") not in want: continue
    print("###", d["kind"], d["name"], "|", d.get("category"))
    print("  contexts:", d.get("compatibleContexts"), "data:", d.get("compatibleData"))
    for s in d.get("settings", []):
        ev = s.get("enumValues")
        print(f'   set {s["name"]} = {s.get("value")}' + (f'  [{", ".join(ev)}]' if ev else ''))
    def walk(slots, d=0):
        for s in slots or []:
            print("   " + "  " * d + "slot", s.get("path"), ":", (s.get("type") or "").split(".")[-1])
            walk(s.get("children"), d + 1)
    walk(d.get("inputSlots"))
PY
```

Matching a node name is loose — punctuation, pipes, underscores and `#0`-style sort prefixes are
ignored, so `"Set Position Shape (Sphere)"` finds `|Set|_Position Shape|Sphere`. Ambiguous names
fail loudly and list the candidates; qualify with the category (`"Force/Turbulence"`) to
disambiguate. Exact internal names and full type names (`UnityEditor.VFX.Block.Turbulence`) also
work.

## Health files

- `VFXAI_Reports/heartbeat.txt` — timestamp plus compiling/playing state, rewritten every 5s.
  Stale means Unity is closed.
- `VFXAI_Reports/editor_status.json` — Unity version, project path, and which tooling
  assemblies actually loaded.
- `VFXAI_Reports/compile_log.json` — errors and warnings from the last script compilation,
  written by an assembly with no dependencies so it survives breakage elsewhere.
