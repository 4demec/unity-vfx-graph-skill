# Spec schema

The `apply` op takes either a `graph` (build) or `edits` (patch). Both may appear together;
`graph` runs first.

```json
{
  "path": "Assets/VFX/Name.vfx",
  "mode": "create" | "replace" | "edit",
  "graph": { "parameters": [], "operators": [], "systems": [], "links": [] },
  "edits": [ { "op": "...", "target": "..." } ]
}
```

- `path` must be under `Assets/` and end in `.vfx`. Missing folders are created.
- `create` fails if the file exists. `replace` wipes the graph and rebuilds. `edit` keeps it and
  applies operations against ids from `inspect`.

## graph

### systems

Each system is a chain of contexts. Contexts are laid out top-to-bottom automatically if you
omit `position`.

```json
{
  "name": "Sparks",
  "contexts": [ { "id": "spawn", "node": "Spawn", "position": [0,0],
                  "settings": {}, "values": {}, "space": "World",
                  "material": {}, "blocks": [] } ],
  "flow": [ ["spawn","init"], ["init","update",0,0] ]
}
```

- `id` — how links and edits address this node. Give one to anything you will reference.
- `settings` — VFX settings, applied before values because some of them change which slots exist.
- `values` — input slot values, keyed by slot path or name.
- `space` — `Local` or `World`, the simulation space of the whole system. Set it on Initialize.
- `material` — shader property overrides; see below.
- `blocks` — ordered; each takes `id`, `node`, `settings`, `values`.
- `flow` — pairs of context ids, with optional from/to flow-slot indices. Omit it and contexts
  chain in declaration order.

A block incompatible with its context is refused and logged rather than silently added — the
builder checks `VFXContext.Accept` first.

### operators

```json
{ "id": "opMul", "node": "Multiply", "position": [-460, 40],
  "settings": {}, "values": { "a": 2.0 } }
```

### parameters (exposed properties / blackboard)

```json
{ "id": "pRate", "name": "EmberRate", "type": "float", "value": 40.0,
  "exposed": true, "category": "Tuning", "position": [-900, -150] }
```

`type` accepts a friendly name (`float`, `int`, `uint`, `bool`, `vector2/3/4`, `color`,
`texture2d`, `mesh`, `gradient`, `curve`) or any full type name from `slot_types.tsv`.
A node is placed in the graph view automatically so the parameter is usable.

### links

Data links between slots. Omit `fromSlot` to use the node's first output — right for most
operators, whose output slot is unnamed.

```json
[
  { "from": "opTime", "to": "opSin", "toSlot": "x" },
  { "from": "pRate",  "to": "opMul", "toSlot": "b" },
  { "from": "opMul",  "to": "rateBlock", "toSlot": "Rate" },
  { "from": "trigger", "fromSlot": "evt", "to": "gpuevt", "toSlot": "evt" }
]
```

Incompatible slot types are refused and logged; Unity decides, not the builder.

## Values

| target type | JSON |
|---|---|
| float / int / uint / bool | `0.5`, `12`, `true` |
| enum setting | `"Additive"` (by name) |
| Vector2/3/4 | `[x, y]`, `[x, y, z]`, `[x, y, z, w]`, or a single number broadcast |
| Color | `[r, g, b]`, `[r, g, b, a]`, or `"#ff8800"` — values above 1 give HDR glow |
| asset reference | `"Assets/Textures/Spark.png"` |
| Gradient | `{"colorKeys": [{"color":[1,0.9,0.5],"time":0}], "alphaKeys": [{"alpha":1,"time":0.1}]}` |
| AnimationCurve | `{"keys": [{"time":0,"value":0.2}, {"time":1,"value":0}]}` |
| composite slot | nested object, or address the leaf directly |

Composite slots (`Vector`, `Position`, `Sphere`, `Transform`, `TArcSphere`) are trees. An array
works when the shape is unambiguous — `"Force": [0,-6,0]` resolves through the single `vector`
child. Otherwise nest (`{"arcSphere": {"sphere": {"radius": 1.5}}}`) or name the leaf
(`"radius": 1.5`). Slot lookup ignores leading underscores, so `"Lifetime"` finds `_Lifetime`.

### Spaces

Spaceable slots carry their own space and VFX Graph converts between them automatically. That
conversion is the mechanism behind world-space trails:

```json
{ "node": "Set Position",
  "values": { "Position": { "space": "Local", "value": [0, 0, 0] } } }
```

In a system whose Initialize context is `"space": "World"`, that local origin becomes the
emitter's current world position at spawn time — the particle is born where the object is now
and then stays put.

### Material overrides

Some outputs — Shader Graph ones especially — keep blending in a material property map rather
than in VFX settings. Those go in `material` on the context, or a `setMaterial` edit:

```json
{ "id": "output", "node": "Output ParticleStrip (Shader Graph) Quad",
  "material": { "_Blend": 2, "_ZWrite": 0 } }
```

These are applied in a second pass, because the material does not exist until the asset has
compiled once. Run `inspect` to see the float properties a given shader exposes; unknown names
are skipped and the log lists the valid ones.

## edits

Every edit takes `target`, an id from `inspect`.

| op | fields | effect |
|---|---|---|
| `setValues` | `values` | set input slot values |
| `setSettings` | `settings` | set VFX settings |
| `setMaterial` | `properties` | shader property overrides |
| `setSpace` | `space`, optional `slot` | system space, or one slot's space |
| `addBlock` | `node`, optional `id`, `index`, `settings`, `values` | insert a block into a context |
| `addNode` | `kind` (`operator`/`parameter`/`context`), `node`, optional `id`, `settings`, `values`, `position` | add a graph-level node; takes no `target` |
| `remove` | — | delete a node and unlink it |
| `link` | `from`, `fromSlot`, `to`, `toSlot` | connect slots |
| `unlink` | `to`, `toSlot` | disconnect an input |
| `linkContexts` | `from`, `to`, `fromIndex`, `toIndex` | flow link |
| `move` | `position` | reposition in the graph view |

```json
{
  "path": "Assets/VFX/Sparks.vfx",
  "mode": "edit",
  "edits": [
    { "op": "setSettings", "target": "c3", "settings": { "blendMode": "Additive" } },
    { "op": "setValues",   "target": "c0.b0", "values": { "Rate": 260 } },
    { "op": "remove",      "target": "c2.b2" },
    { "op": "addBlock",    "target": "c3", "node": "Orient (Along Velocity)" }
  ]
}
```

Edits run in order against ids resolved up front, so removing a node does not renumber the ids
used by later edits in the same job.
