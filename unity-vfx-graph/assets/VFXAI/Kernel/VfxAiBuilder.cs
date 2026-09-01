// VfxAiBuilder.cs
// Creates and edits .vfx assets through the Visual Effect Graph editor model.
//
// Every API used here was read out of the installed package source
// (Library/PackageCache/com.unity.visualeffectgraph@.../Editor), not assumed:
//   VFXLibrary.GetContexts/GetBlocks/GetOperators/GetParameters -> VFXModelDescriptor<T>
//   VisualEffectAssetEditorUtility.CreateNewAsset(path)
//   VisualEffectResource.GetResourceAtPath(path) / GetOrCreateGraph() / WriteAssetWithSubAssets()
//   VFXModel.AddChild/RemoveChild/SetSettingValue/GetSettings/position
//   VFXContext.LinkTo/CanLink/Accept, VFXSlot.Link/CanLink/value
//   VFXGraph.SetExpressionGraphDirty/RecompileIfNeeded
// None of this is public Unity API. It is version-checked at runtime and re-verified by
// compiling and importing the asset Unity itself produces.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.VFX;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.VFX;
using UnityObject = UnityEngine.Object;

namespace VfxAi.Kernel
{
    public static class VfxAiBuilder
    {
        /// <summary>A material-property write, deferred until after the first compile creates the material.</summary>
        class MaterialRequest
        {
            public VFXModel model;
            public string id;
            public Dictionary<string, object> properties;
        }

        // ============================================================== public ops

        /// <summary>op "list": every .vfx / .vfxoperator / .vfxblock asset in the project.</summary>
        public static string List(Dictionary<string, object> args)
        {
            var w = OkHeader();
            w.BeginArray("assets");
            foreach (var guid in AssetDatabase.FindAssets("t:VisualEffectAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                w.BeginObject();
                w.Prop("path", path);
                w.Prop("name", Path.GetFileNameWithoutExtension(path));
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        /// <summary>op "inspect": read an existing graph back out as an addressable description.</summary>
        public static string Inspect(Dictionary<string, object> args)
        {
            var path = JsonReader.GetString(args, "path");
            if (string.IsNullOrEmpty(path)) return Fail("inspect requires \"path\"");
            if (!File.Exists(path)) return Fail("no asset at '" + path + "'");

            var resource = VisualEffectResource.GetResourceAtPath(path);
            if (resource == null) return Fail("'" + path + "' is not a visual effect asset");

            var graph = resource.GetOrCreateGraph();
            if (graph == null) return Fail("could not open the graph in '" + path + "'");

            var ids = BuildIdMap(graph);

            var w = OkHeader();
            w.Prop("path", path);
            w.BeginArray("nodes");
            foreach (var kv in ids)
            {
                var model = kv.Value;
                w.BeginObject();
                w.Prop("id", kv.Key);
                w.Prop("kind", KindOf(model));
                w.Prop("type", model.GetType().FullName);
                w.Prop("label", DisplayName(model));
                try { w.Key("position").BeginArray().Value(model.position.x).Value(model.position.y).EndArray(); } catch { }

                var asContext = model as VFXContext;
                if (asContext != null)
                {
                    if (asContext.spaceable) w.Prop("space", Try(() => asContext.space.ToString()));
                    // the name a human typed on the context header, e.g. "SYSTEM:" in Unity's samples
                    var userLabel = Try(() => asContext.label);
                    if (!string.IsNullOrEmpty(userLabel)) w.Prop("userLabel", userLabel);
                }

                var param = model as VFXParameter;
                if (param != null)
                {
                    w.Prop("exposedName", Try(() => param.exposedName));
                    w.Prop("exposed", param.exposed);
                    w.Prop("category", Try(() => param.category));
                    w.Prop("valueType", Try(() => param.type != null ? param.type.FullName : null));
                    w.Prop("value", Try(() => VfxAiCatalog.Stringify(param.value)));
                }

                WriteSettings(w, model);
                WriteMaterial(w, model);
                WriteSlots(w, model, ids);
                w.EndObject();
            }
            w.EndArray();

            WriteAnnotations(w, graph, ids);

            // context-to-context flow
            w.BeginArray("flow");
            foreach (var kv in ids)
            {
                var ctx = kv.Value as VFXContext;
                if (ctx == null) continue;
                for (int i = 0; i < ctx.outputFlowSlot.Length; i++)
                {
                    foreach (var link in ctx.outputFlowSlot[i].link)
                    {
                        string toId;
                        if (link.context == null || !TryGetId(ids, link.context, out toId)) continue;
                        w.BeginObject();
                        w.Prop("from", kv.Key);
                        w.Prop("fromIndex", i);
                        w.Prop("to", toId);
                        w.Prop("toIndex", link.slotIndex);
                        w.EndObject();
                    }
                }
            }
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        /// <summary>
        /// op "scene": put the effect in the open scene so it can actually be judged. Some effects
        /// (trails above all) are meaningless standing still, so this can also attach a mover.
        /// </summary>
        public static string Scene(Dictionary<string, object> args)
        {
            var log = new List<string>();
            var assetPath = JsonReader.GetString(args, "asset");
            if (string.IsNullOrEmpty(assetPath)) return Fail("scene requires \"asset\"");

            var asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
            if (asset == null) return Fail("no VisualEffectAsset at '" + assetPath + "'");

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return Fail("no scene is currently open");

            var goName = JsonReader.GetString(args, "name", Path.GetFileNameWithoutExtension(assetPath) + " Preview");

            GameObject go = null;
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == goName) { go = root; break; }

            if (go == null)
            {
                go = new GameObject(goName);
                Undo.RegisterCreatedObjectUndo(go, "Create VFX preview");
                log.Add("created GameObject '" + goName + "'");
            }
            else log.Add("reused existing GameObject '" + goName + "'");

            var posArr = JsonReader.GetArray(args, "position");
            if (posArr != null && posArr.Count >= 3)
                go.transform.position = new Vector3(ToFloat(posArr[0]), ToFloat(posArr[1]), ToFloat(posArr[2]));

            var vfx = go.GetComponent<VisualEffect>();
            if (vfx == null) vfx = Undo.AddComponent<VisualEffect>(go);
            vfx.visualEffectAsset = asset;
            log.Add("VisualEffect -> " + assetPath);

            var mover = JsonReader.GetObject(args, "mover");
            if (mover != null) AttachMover(go, mover, log);

            EditorSceneManager.MarkSceneDirty(scene);
            if (JsonReader.GetBool(args, "save", false) && !string.IsNullOrEmpty(scene.path))
            {
                EditorSceneManager.SaveScene(scene);
                log.Add("saved scene " + scene.path);
            }

            if (JsonReader.GetBool(args, "select", true))
            {
                Selection.activeGameObject = go;
                if (SceneView.lastActiveSceneView != null)
                {
                    SceneView.lastActiveSceneView.FrameSelected();
                    SceneView.lastActiveSceneView.Focus();
                }
            }

            if (JsonReader.GetBool(args, "openGraph", false))
            {
                AssetDatabase.OpenAsset(asset);
                log.Add("opened the graph editor");
            }

            var w = OkHeader();
            w.Prop("asset", assetPath);
            w.Prop("gameObject", goName);
            w.Prop("scene", scene.name);
            w.BeginArray("log");
            foreach (var l in log) w.Value(l);
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        static void AttachMover(GameObject go, Dictionary<string, object> spec, List<string> log)
        {
            Type moverType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { moverType = asm.GetType("VfxAiPreviewMover", false); }
                catch { }
                if (moverType != null) break;
            }

            if (moverType == null)
            {
                log.Add("SKIPPED mover: VfxAiPreviewMover script not found (is Assets/VFXAI/Runtime imported?)");
                return;
            }

            var component = go.GetComponent(moverType);
            if (component == null) component = Undo.AddComponent(go, moverType);
            if (component == null) { log.Add("SKIPPED mover: could not add the component"); return; }

            foreach (var kv in spec)
            {
                var field = moverType.GetField(kv.Key,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (field == null)
                {
                    var names = moverType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Select(f => f.Name).Take(10).ToArray();
                    log.Add("SKIPPED mover field '" + kv.Key + "' - available: " + string.Join(", ", names));
                    continue;
                }
                var value = ConvertValue(field.FieldType, kv.Value, log);
                if (value == null) continue;
                field.SetValue(component, value);
                log.Add("mover." + kv.Key + " = " + VfxAiCatalog.Stringify(value));
            }
        }

        /// <summary>op "apply": build a new graph, or patch an existing one with edit operations.</summary>
        public static string Apply(Dictionary<string, object> args)
        {
            var log = new List<string>();
            var path = JsonReader.GetString(args, "path");
            if (string.IsNullOrEmpty(path)) return Fail("apply requires \"path\"");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return Fail("path must be inside Assets/ (got '" + path + "')");
            if (!path.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase))
                return Fail("path must end in .vfx (got '" + path + "')");

            var mode = (JsonReader.GetString(args, "mode", "create") ?? "create").ToLowerInvariant();
            bool exists = File.Exists(path);

            if (mode == "create" && exists)
                return Fail("'" + path + "' already exists; use mode \"replace\" to overwrite or \"edit\" to patch it");
            if ((mode == "edit" || mode == "replace") && !exists)
                return Fail("'" + path + "' does not exist; use mode \"create\"");

            try
            {
                if (!exists)
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                        AssetDatabase.Refresh();
                        log.Add("created folder " + dir);
                    }
                    var created = VisualEffectAssetEditorUtility.CreateNewAsset(path);
                    if (created == null) return Fail("Unity refused to create an asset at '" + path + "'");
                    log.Add("created empty asset " + path);
                }

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

                var resource = VisualEffectResource.GetResourceAtPath(path);
                if (resource == null) return Fail("could not load the visual effect resource for '" + path + "'");
                var graph = resource.GetOrCreateGraph();
                if (graph == null) return Fail("could not open the graph for '" + path + "'");

                if (mode == "replace")
                {
                    var doomed = graph.children.ToList();
                    foreach (var child in doomed)
                    {
                        VFXModel.UnlinkModel(child);
                        graph.RemoveChild(child);
                    }
                    log.Add("cleared " + doomed.Count + " existing node(s)");
                }

                var byId = new Dictionary<string, VFXModel>(StringComparer.Ordinal);
                if (mode == "edit")
                    foreach (var kv in BuildIdMap(graph)) byId[kv.Key] = kv.Value;

                var materialRequests = new List<MaterialRequest>();

                var graphSpec = JsonReader.GetObject(args, "graph");
                if (graphSpec != null) BuildGraph(graph, graphSpec, byId, materialRequests, log);

                var edits = JsonReader.GetArray(args, "edits");
                if (edits != null) ApplyEdits(graph, edits, byId, materialRequests, log);

                if (graphSpec == null && edits == null)
                    return Fail("apply needs either \"graph\" (to build) or \"edits\" (to patch)");

                // Persist, then let Unity be the judge of whether the graph is valid. Anything the
                // compiler or importer complains about lands in the console, so capture it.
                var errors = new List<string>();
                Application.LogCallback capture = (condition, stackTrace, type) =>
                {
                    if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                        errors.Add(condition);
                };

                Application.logMessageReceived += capture;
                try
                {
                    graph.SetExpressionGraphDirty();
                    graph.RecompileIfNeeded();
                    resource.WriteAssetWithSubAssets();
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

                    // Material overrides can only be written once compilation has created the
                    // material, so they need a second pass.
                    if (materialRequests.Count > 0)
                    {
                        foreach (var req in materialRequests)
                            ApplyMaterialProperties(req, log);

                        graph.SetExpressionGraphDirty();
                        graph.RecompileIfNeeded();
                        resource.WriteAssetWithSubAssets();
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    }
                }
                finally
                {
                    Application.logMessageReceived -= capture;
                }

                var w = OkHeader();
                w.Prop("path", path);
                w.Prop("mode", mode);
                w.BeginArray("log");
                foreach (var l in log) w.Value(l);
                w.EndArray();
                w.BeginArray("graphErrors");
                foreach (var e in errors) w.Value(e);
                w.EndArray();
                w.Prop("nodeCount", graph.GetNbChildren());
                w.EndObject();
                return w.ToString();
            }
            catch (Exception e)
            {
                var w = new JsonWriter(true);
                w.BeginObject();
                w.Prop("status", "error");
                w.Prop("message", e.GetType().Name + ": " + e.Message);
                w.Prop("stack", e.StackTrace);
                w.BeginArray("log");
                foreach (var l in log) w.Value(l);
                w.EndArray();
                w.EndObject();
                return w.ToString();
            }
        }

        // ============================================================== graph build

        static void BuildGraph(VFXGraph graph, Dictionary<string, object> spec,
                               Dictionary<string, VFXModel> byId, List<MaterialRequest> materialRequests, List<string> log)
        {
            // 1. exposed properties / blackboard
            var parameters = JsonReader.GetArray(spec, "parameters");
            if (parameters != null)
            {
                foreach (var raw in parameters)
                {
                    var p = JsonReader.AsObject(raw);
                    if (p == null) continue;
                    var model = CreateParameter(p, log);
                    if (model == null) continue;
                    graph.AddChild(model);
                    Register(byId, JsonReader.GetString(p, "id"), model, log);
                    PlaceNode(model, p);
                }
            }

            // 2. free-floating operators
            var operators = JsonReader.GetArray(spec, "operators");
            if (operators != null)
            {
                foreach (var raw in operators)
                {
                    var o = JsonReader.AsObject(raw);
                    if (o == null) continue;
                    var desc = ResolveDescriptor("operator", JsonReader.GetString(o, "node"), log);
                    if (desc == null) continue;
                    var model = desc.CreateInstance();
                    graph.AddChild(model);
                    ApplySettings(model, JsonReader.GetObject(o, "settings"), log);
                    ApplyValues(model, JsonReader.GetObject(o, "values"), log);
                    Register(byId, JsonReader.GetString(o, "id"), model, log);
                    PlaceNode(model, o);
                }
            }

            // 3. systems: contexts + blocks + vertical flow
            var systems = JsonReader.GetArray(spec, "systems");
            if (systems != null)
            {
                foreach (var rawSys in systems)
                {
                    var sys = JsonReader.AsObject(rawSys);
                    if (sys == null) continue;
                    var sysName = JsonReader.GetString(sys, "name", "System");
                    var contexts = JsonReader.GetArray(sys, "contexts");
                    if (contexts == null) { log.Add("system '" + sysName + "' has no contexts"); continue; }

                    var localOrder = new List<VFXContext>();

                    foreach (var rawCtx in contexts)
                    {
                        var c = JsonReader.AsObject(rawCtx);
                        if (c == null) continue;
                        var desc = ResolveDescriptor("context", JsonReader.GetString(c, "node"), log);
                        if (desc == null) continue;

                        var ctx = desc.CreateInstance() as VFXContext;
                        if (ctx == null) { log.Add("'" + JsonReader.GetString(c, "node") + "' is not a context"); continue; }

                        graph.AddChild(ctx);
                        ApplySettings(ctx, JsonReader.GetObject(c, "settings"), log);
                        ApplyContextSpace(ctx, JsonReader.GetString(c, "space"), log);

                        var blocks = JsonReader.GetArray(c, "blocks");
                        if (blocks != null)
                        {
                            foreach (var rawBlock in blocks)
                            {
                                var b = JsonReader.AsObject(rawBlock);
                                if (b == null) continue;
                                var bdesc = ResolveDescriptor("block", JsonReader.GetString(b, "node"), log);
                                if (bdesc == null) continue;

                                var block = bdesc.CreateInstance() as VFXBlock;
                                if (block == null) { log.Add("'" + JsonReader.GetString(b, "node") + "' is not a block"); continue; }

                                if (!ctx.Accept(block))
                                {
                                    log.Add("REJECTED: block '" + bdesc.name + "' is not compatible with context '"
                                            + desc.name + "' (needs " + block.compatibleContexts + " / " + block.compatibleData + ")");
                                    UnityObject.DestroyImmediate(block);
                                    continue;
                                }

                                ctx.AddChild(block);
                                ApplySettings(block, JsonReader.GetObject(b, "settings"), log);
                                ApplyValues(block, JsonReader.GetObject(b, "values"), log);
                                Register(byId, JsonReader.GetString(b, "id"), block, log);
                            }
                        }

                        // values after blocks: some context settings resize their slots
                        ApplyValues(ctx, JsonReader.GetObject(c, "values"), log);

                        var material = JsonReader.GetObject(c, "material");
                        if (material != null)
                            materialRequests.Add(new MaterialRequest
                            {
                                model = ctx,
                                id = JsonReader.GetString(c, "id", desc.name),
                                properties = material
                            });

                        Register(byId, JsonReader.GetString(c, "id"), ctx, log);
                        PlaceNode(ctx, c);
                        localOrder.Add(ctx);
                    }

                    // explicit flow, else chain the contexts in declaration order
                    var flow = JsonReader.GetArray(sys, "flow");
                    if (flow != null)
                    {
                        foreach (var rawLink in flow)
                        {
                            var l = JsonReader.AsArray(rawLink);
                            if (l == null || l.Count < 2) continue;
                            LinkContexts(byId, l[0] as string, l[1] as string,
                                l.Count > 2 ? ToInt(l[2]) : 0, l.Count > 3 ? ToInt(l[3]) : 0, log);
                        }
                    }
                    else
                    {
                        for (int i = 0; i + 1 < localOrder.Count; i++)
                            TryLink(localOrder[i], localOrder[i + 1], 0, 0, log);
                    }

                    AutoLayout(localOrder);
                }
            }

            // 4. data links, once every node exists
            var links = JsonReader.GetArray(spec, "links");
            if (links != null)
                foreach (var raw in links)
                    ApplyLink(JsonReader.AsObject(raw), byId, log);
        }

        static void ApplyEdits(VFXGraph graph, List<object> edits,
                               Dictionary<string, VFXModel> byId, List<MaterialRequest> materialRequests, List<string> log)
        {
            foreach (var raw in edits)
            {
                var e = JsonReader.AsObject(raw);
                if (e == null) continue;
                var op = (JsonReader.GetString(e, "op", "") ?? "").ToLowerInvariant();
                var targetId = JsonReader.GetString(e, "target");
                VFXModel target = null;
                if (!string.IsNullOrEmpty(targetId) && !byId.TryGetValue(targetId, out target))
                {
                    log.Add("SKIPPED " + op + ": no node with id '" + targetId + "'");
                    continue;
                }

                switch (op)
                {
                    case "setvalues":
                        ApplyValues(target, JsonReader.GetObject(e, "values"), log);
                        break;

                    case "setsettings":
                        ApplySettings(target, JsonReader.GetObject(e, "settings"), log);
                        break;

                    case "setmaterial":
                    {
                        var props = JsonReader.GetObject(e, "properties");
                        if (target == null || props == null)
                        {
                            log.Add("SKIPPED setMaterial: needs \"target\" and \"properties\"");
                            break;
                        }
                        materialRequests.Add(new MaterialRequest { model = target, id = targetId, properties = props });
                        break;
                    }

                    case "addblock":
                    {
                        var ctx = target as VFXContext;
                        if (ctx == null) { log.Add("SKIPPED addBlock: '" + targetId + "' is not a context"); break; }
                        var desc = ResolveDescriptor("block", JsonReader.GetString(e, "node"), log);
                        if (desc == null) break;
                        var block = desc.CreateInstance() as VFXBlock;
                        if (block == null) break;
                        if (!ctx.Accept(block))
                        {
                            log.Add("REJECTED addBlock: '" + desc.name + "' is not compatible with that context");
                            UnityObject.DestroyImmediate(block);
                            break;
                        }
                        ctx.AddChild(block, JsonReader.GetInt(e, "index", -1));
                        ApplySettings(block, JsonReader.GetObject(e, "settings"), log);
                        ApplyValues(block, JsonReader.GetObject(e, "values"), log);
                        Register(byId, JsonReader.GetString(e, "id"), block, log);
                        log.Add("added block '" + desc.name + "' to " + targetId);
                        break;
                    }

                    case "addnode":
                    {
                        // operators and parameters are graph-level nodes, so unlike addBlock this
                        // takes no target - it attaches straight to the graph
                        var kind = (JsonReader.GetString(e, "kind", "operator") ?? "operator").ToLowerInvariant();
                        VFXModel created = null;

                        if (kind == "parameter")
                        {
                            created = CreateParameter(e, log);
                        }
                        else
                        {
                            var desc = ResolveDescriptor(kind, JsonReader.GetString(e, "node"), log);
                            if (desc == null) break;
                            created = desc.CreateInstance();
                        }
                        if (created == null) break;

                        graph.AddChild(created);
                        ApplySettings(created, JsonReader.GetObject(e, "settings"), log);

                        var asCtx = created as VFXContext;
                        if (asCtx != null) ApplyContextSpace(asCtx, JsonReader.GetString(e, "space"), log);

                        ApplyValues(created, JsonReader.GetObject(e, "values"), log);
                        Register(byId, JsonReader.GetString(e, "id"), created, log);
                        PlaceNode(created, e);
                        log.Add("added " + kind + " '" + DisplayName(created) + "'");
                        break;
                    }

                    case "remove":
                    {
                        if (target == null) { log.Add("SKIPPED remove: needs \"target\""); break; }
                        VFXModel.RemoveModel(target);
                        byId.Remove(targetId);
                        log.Add("removed " + targetId);
                        break;
                    }

                    case "link":
                        ApplyLink(e, byId, log);
                        break;

                    case "unlink":
                    {
                        var slot = ResolveSlotRef(byId, JsonReader.GetString(e, "to"), JsonReader.GetString(e, "toSlot"), true, log);
                        if (slot != null) { slot.UnlinkAll(); log.Add("unlinked " + JsonReader.GetString(e, "to")); }
                        break;
                    }

                    case "linkcontexts":
                        LinkContexts(byId, JsonReader.GetString(e, "from"), JsonReader.GetString(e, "to"),
                            JsonReader.GetInt(e, "fromIndex", 0), JsonReader.GetInt(e, "toIndex", 0), log);
                        break;

                    case "setspace":
                    {
                        var spaceName = JsonReader.GetString(e, "space");
                        var slotPath = JsonReader.GetString(e, "slot");
                        if (string.IsNullOrEmpty(slotPath))
                        {
                            ApplyContextSpace(target as VFXContext, spaceName, log);
                            break;
                        }
                        var container = target as IVFXSlotContainer;
                        var slot = container != null ? FindSlot(container.inputSlots, slotPath) : null;
                        if (slot == null) { log.Add("SKIPPED setSpace: no input slot '" + slotPath + "' on " + targetId); break; }
                        object parsed;
                        if (!slot.spaceable) { log.Add("SKIPPED setSpace: slot '" + slotPath + "' has no space"); break; }
                        if (!TryParseSpace(spaceName, out parsed, log)) break;
                        slot.space = (VFXSpace)parsed;
                        log.Add("space of " + targetId + "." + slotPath + " = " + spaceName);
                        break;
                    }

                    case "move":
                        if (target != null) PlaceNode(target, e);
                        break;

                    default:
                        log.Add("SKIPPED: unknown edit op '" + op + "'");
                        break;
                }
            }
        }

        // ============================================================== nodes

        static VFXParameter CreateParameter(Dictionary<string, object> spec, List<string> log)
        {
            var typeName = JsonReader.GetString(spec, "type", "System.Single");
            var type = ResolveSlotType(typeName);
            if (type == null)
            {
                log.Add("SKIPPED parameter: unknown type '" + typeName + "'");
                return null;
            }

            var descriptor = VFXLibrary.GetParameters().FirstOrDefault(d => d.modelType == type);
            VFXParameter param;
            if (descriptor != null)
            {
                param = descriptor.CreateInstance();
            }
            else
            {
                param = ScriptableObject.CreateInstance<VFXParameter>();
                param.Init(type);
            }

            // exposedName / exposed are get-only properties backed by [VFXSetting] fields
            var name = JsonReader.GetString(spec, "name", "Parameter");
            param.SetSettingValue("m_ExposedName", name);
            param.SetSettingValue("m_Exposed", JsonReader.GetBool(spec, "exposed", true));

            var category = JsonReader.GetString(spec, "category");
            if (!string.IsNullOrEmpty(category)) param.category = category;

            object v;
            if (spec.TryGetValue("value", out v) && v != null)
            {
                var converted = ConvertValue(type, v, log);
                if (converted != null) param.value = converted;
            }

            // a parameter needs at least one node in the graph view to be usable
            var pos = ReadPosition(spec);
            param.CreateDefaultNode(pos ?? Vector2.zero);

            log.Add("added parameter '" + name + "' (" + type.Name + ")");
            return param;
        }

        static void Register(Dictionary<string, VFXModel> byId, string id, VFXModel model, List<string> log)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (byId.ContainsKey(id)) log.Add("WARNING: duplicate id '" + id + "', later node wins");
            byId[id] = model;
        }

        static void PlaceNode(VFXModel model, Dictionary<string, object> spec)
        {
            var p = ReadPosition(spec);
            if (p.HasValue) model.position = p.Value;
        }

        static Vector2? ReadPosition(Dictionary<string, object> spec)
        {
            var arr = JsonReader.GetArray(spec, "position");
            if (arr == null || arr.Count < 2) return null;
            return new Vector2(ToFloat(arr[0]), ToFloat(arr[1]));
        }

        static void AutoLayout(List<VFXContext> contexts)
        {
            // only lay out nodes that were left at the origin
            float y = 0f;
            foreach (var c in contexts)
            {
                if (c.position != Vector2.zero) { y = c.position.y + 220f; continue; }
                c.position = new Vector2(0f, y);
                y += 220f;
            }
        }

        // ============================================================== descriptors

        static IVFXModelDescriptor ResolveDescriptor(string kind, string query, List<string> log)
        {
            if (string.IsNullOrEmpty(query))
            {
                log.Add("SKIPPED: a " + kind + " entry is missing \"node\"");
                return null;
            }

            var all = DescriptorsOf(kind);
            var matches = new List<IVFXModelDescriptor>();
            var loose = new List<IVFXModelDescriptor>();
            var normalizedQuery = Normalize(query);

            foreach (var d in all)
            {
                string name = null, category = null, typeName = null;
                try { name = d.name; category = d.category; typeName = d.modelType != null ? d.modelType.FullName : null; } catch { }
                if (name == null) continue;

                var full = string.IsNullOrEmpty(category) ? name : category + "/" + name;
                if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(full, query, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(typeName, query, StringComparison.Ordinal))
                {
                    matches.Add(d);
                    continue;
                }

                // Internal names are pipe-delimited with sort prefixes ("|Set|_Lifetime|Random Uniform",
                // category "#1Set"). Let callers write them the way the UI shows them.
                if (Normalize(name) == normalizedQuery || Normalize(full) == normalizedQuery)
                    loose.Add(d);
            }

            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0 && loose.Count == 1) return loose[0];
            if (matches.Count == 0 && loose.Count > 1) matches = loose;

            if (matches.Count == 1) return matches[0];

            if (matches.Count == 0)
            {
                var near = all
                    .Select(d => { try { return d.name; } catch { return null; } })
                    .Where(n => !string.IsNullOrEmpty(n)
                                && n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Distinct().Take(8).ToArray();
                log.Add("SKIPPED: no " + kind + " named '" + query + "'"
                        + (near.Length > 0 ? " - did you mean: " + string.Join(", ", near) : ""));
                return null;
            }

            var options = matches.Select(d => (d.category ?? "") + "/" + d.name).Take(8).ToArray();
            log.Add("SKIPPED: '" + query + "' is ambiguous, qualify it with a category - " + string.Join(", ", options));
            return null;
        }

        /// <summary>
        /// Folds VFX Graph's internal node naming into something a human would type.
        /// "#1Set/|Set|_Lifetime|Random Uniform" and "Set Lifetime (Random Uniform)" both become
        /// "set set lifetime random uniform" / "set lifetime random uniform".
        /// </summary>
        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '#')
                {
                    // strip the "#0"/"#1" ordering prefixes baked into category names
                    while (i + 1 < s.Length && char.IsDigit(s[i + 1])) i++;
                    continue;
                }
                if (c == '|' || c == '_' || c == '/' || c == '(' || c == ')' || c == ',' || c == '-' || c == ':' || c == '&')
                {
                    sb.Append(' ');
                    continue;
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            var parts = sb.ToString().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }

        static List<IVFXModelDescriptor> DescriptorsOf(string kind)
        {
            var list = new List<IVFXModelDescriptor>();
            try
            {
                IEnumerable<IVFXModelDescriptor> top;
                switch (kind)
                {
                    case "context": top = VFXLibrary.GetContexts().Cast<IVFXModelDescriptor>(); break;
                    case "block": top = VFXLibrary.GetBlocks().Cast<IVFXModelDescriptor>(); break;
                    case "operator": top = VFXLibrary.GetOperators().Cast<IVFXModelDescriptor>(); break;
                    case "parameter": top = VFXLibrary.GetParameters().Cast<IVFXModelDescriptor>(); break;
                    default: return list;
                }

                foreach (var d in top)
                {
                    list.Add(d);
                    IVFXModelDescriptor[] subs = null;
                    try { subs = d.subVariantDescriptors; } catch { }
                    if (subs != null) list.AddRange(subs);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VFX AI] descriptor query failed: " + e.Message);
            }
            return list;
        }

        static Type ResolveSlotType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var t in VFXLibrary.GetSlotsType())
            {
                if (string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            switch (name.ToLowerInvariant())
            {
                case "float": return typeof(float);
                case "int": return typeof(int);
                case "uint": return typeof(uint);
                case "bool": return typeof(bool);
                case "vector2": return typeof(Vector2);
                case "vector3": return typeof(Vector3);
                case "vector4": return typeof(Vector4);
                case "color": return typeof(Color);
                case "texture2d": return typeof(Texture2D);
                case "mesh": return typeof(Mesh);
                case "gradient": return typeof(Gradient);
                case "animationcurve": case "curve": return typeof(AnimationCurve);
            }
            return null;
        }

        // ============================================================== settings & values

        static void ApplySettings(VFXModel model, Dictionary<string, object> settings, List<string> log)
        {
            if (model == null || settings == null) return;
            foreach (var kv in settings)
            {
                try
                {
                    var setting = model.GetSetting(kv.Key);
                    if (!setting.valid)
                    {
                        var available = model.GetSettings(true).Where(s => s.valid).Select(s => s.name).Take(12).ToArray();
                        log.Add("SKIPPED setting '" + kv.Key + "' on " + DisplayName(model)
                                + " - available: " + string.Join(", ", available));
                        continue;
                    }
                    var value = ConvertValue(setting.field.FieldType, kv.Value, log);
                    if (value == null)
                    {
                        log.Add("SKIPPED setting '" + kv.Key + "': could not read a "
                                + setting.field.FieldType.Name + " from the value given");
                        continue;
                    }
                    model.SetSettingValue(kv.Key, value);
                }
                catch (Exception e)
                {
                    log.Add("SKIPPED setting '" + kv.Key + "': " + e.Message);
                }
            }
        }

        static void ApplyValues(VFXModel model, Dictionary<string, object> values, List<string> log)
        {
            if (model == null || values == null) return;
            var container = model as IVFXSlotContainer;
            if (container == null)
            {
                log.Add("SKIPPED values on " + DisplayName(model) + ": it has no slots");
                return;
            }

            foreach (var kv in values)
            {
                var slot = FindSlot(container.inputSlots, kv.Key);
                if (slot == null)
                {
                    var paths = AllSlotPaths(container.inputSlots).Take(16).ToArray();
                    log.Add("SKIPPED value '" + kv.Key + "' on " + DisplayName(model)
                            + " - input slots are: " + string.Join(", ", paths));
                    continue;
                }
                SetSlotValue(slot, kv.Value, log);
            }
        }

        /// <summary>
        /// Sets the simulation space of a whole system. This is what decides whether particles ride
        /// along with the GameObject (Local) or stay where they were born (World) - the difference
        /// between a ribbon that follows an object and a trail left behind it.
        /// </summary>
        static void ApplyContextSpace(VFXContext ctx, string spaceName, List<string> log)
        {
            if (ctx == null || string.IsNullOrEmpty(spaceName)) return;
            if (!ctx.spaceable)
            {
                log.Add("SKIPPED space on " + DisplayName(ctx) + ": this context has no simulation space");
                return;
            }
            object parsed;
            if (!TryParseSpace(spaceName, out parsed, log)) return;
            ctx.space = (VFXSpace)parsed;
            log.Add("space of " + DisplayName(ctx) + " = " + spaceName);
        }

        static bool TryParseSpace(string name, out object value, List<string> log)
        {
            value = null;
            try
            {
                value = Enum.Parse(typeof(VFXSpace), name, true);
                return true;
            }
            catch
            {
                log.Add("SKIPPED space '" + name + "': expected one of "
                        + string.Join(", ", Enum.GetNames(typeof(VFXSpace))));
                return false;
            }
        }

        static void SetSlotValue(VFXSlot slot, object json, List<string> log)
        {
            // { "space": "Local", "value": [...] } - a spaceable slot carries its own space, and VFX
            // Graph converts automatically when it feeds a system simulating in a different one.
            var wrapper = JsonReader.AsObject(json);
            if (wrapper != null && wrapper.ContainsKey("space"))
            {
                var spaceName = JsonReader.GetString(wrapper, "space");
                if (!slot.spaceable)
                {
                    log.Add("SKIPPED space on '" + Try(() => slot.path) + "': that slot type has no space");
                }
                else
                {
                    object parsed;
                    if (TryParseSpace(spaceName, out parsed, log))
                    {
                        slot.space = (VFXSpace)parsed;
                        log.Add("space of " + Try(() => slot.path) + " = " + spaceName);
                    }
                }

                object inner;
                if (!wrapper.TryGetValue("value", out inner)) return;
                json = inner;
            }

            Type t = null;
            try { t = slot.property.type; } catch { }

            if (t != null)
            {
                var converted = ConvertValue(t, json, log);
                if (converted != null)
                {
                    try { slot.value = converted; return; }
                    catch (Exception e) { log.Add("could not set '" + slot.path + "': " + e.Message); }
                }
            }

            // composite slot (Position, Sphere, Transform...): fall through to its children
            List<VFXSlot> kids = null;
            try { kids = slot.children.ToList(); } catch { }
            if (kids == null || kids.Count == 0)
            {
                log.Add("SKIPPED '" + slot.path + "': expected " + (t != null ? t.Name : "?") + ", could not convert value");
                return;
            }

            var obj = JsonReader.AsObject(json);
            if (obj != null)
            {
                foreach (var kv in obj)
                {
                    var child = kids.FirstOrDefault(k => string.Equals(k.name, kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (child == null)
                    {
                        log.Add("SKIPPED '" + slot.path + "." + kv.Key + "': no such sub-slot ("
                                + string.Join(", ", kids.Select(k => k.name).ToArray()) + ")");
                        continue;
                    }
                    SetSlotValue(child, kv.Value, log);
                }
                return;
            }

            if (kids.Count == 1)
            {
                SetSlotValue(kids[0], json, log);
                return;
            }

            log.Add("SKIPPED '" + slot.path + "': it is a composite slot, pass an object with keys "
                    + string.Join(", ", kids.Select(k => k.name).ToArray()));
        }

        /// <summary>
        /// Writes shader/material overrides (blend mode on Shader Graph outputs, for instance) which
        /// are NOT VFX settings - they live in a material property map on the output. Mirrors what the
        /// inspector does: apply stored overrides to the material, change it, sync back.
        /// </summary>
        static void ApplyMaterialProperties(MaterialRequest req, List<string> log)
        {
            var output = req.model as VFXAbstractRenderedOutput;
            if (output == null)
            {
                log.Add("SKIPPED material on '" + req.id + "': not a rendered output context");
                return;
            }

            var material = output.FindMaterial();
            if (material == null)
            {
                log.Add("SKIPPED material on '" + req.id + "': no material exists yet for this output");
                return;
            }

            var settings = output.GetSettingValue("materialSettings") as VFXMaterialSerializedSettings;
            if (settings == null)
            {
                log.Add("SKIPPED material on '" + req.id + "': this output has no materialSettings");
                return;
            }

            settings.ApplyToMaterial(material);

            bool changed = false;
            foreach (var kv in req.properties)
            {
                if (!material.HasProperty(kv.Key))
                {
                    log.Add("SKIPPED material property '" + kv.Key + "' on '" + req.id
                            + "' - shader exposes: " + string.Join(", ", FloatPropertyNames(material).Take(14).ToArray()));
                    continue;
                }
                var v = ToFloat(kv.Value);
                material.SetFloat(kv.Key, v);
                log.Add("material " + req.id + "." + kv.Key + " = " + v.ToString(CultureInfo.InvariantCulture));
                changed = true;
            }

            if (!changed) return;

            // Writing the floats is only half of it. URP derives blend state, shader keywords and
            // render queue from them via ShaderUtils.UpdateMaterial, which the SRP binder calls -
            // skip this and the material keeps its old rendering state while reporting new values,
            // which shows up as opaque quads where transparency was expected.
            try
            {
                var binder = VFXLibrary.currentSRPBinder;
                if (binder != null)
                {
                    bool motion = false;
                    try { motion = output.hasMotionVector; } catch { }

                    bool shadows = false;
                    try
                    {
                        var castShadows = output.GetSettingValue("castShadows");
                        if (castShadows is bool) shadows = (bool)castShadows;
                    }
                    catch { }

                    var sg = SafeGetSetting(output, "shaderGraph") as ShaderGraphVfxAsset;
                    binder.SetupMaterial(material, motion, shadows, sg);
                    log.Add("re-applied shader keywords via " + binder.GetType().Name);
                }
                else log.Add("WARNING: no SRP binder, material keywords may be stale");
            }
            catch (Exception e)
            {
                log.Add("WARNING: SetupMaterial failed (" + e.Message + "); blend state may be stale");
            }

            settings.SyncFromMaterial(material);
            output.Invalidate(VFXModel.InvalidationCause.kSettingChanged);
        }

        static object SafeGetSetting(VFXModel model, string name)
        {
            try { return model.GetSettingValue(name); } catch { return null; }
        }

        static IEnumerable<string> FloatPropertyNames(Material material)
        {
            var shader = material != null ? material.shader : null;
            if (shader == null) yield break;
            int count = 0;
            try { count = ShaderUtil.GetPropertyCount(shader); } catch { yield break; }
            for (int i = 0; i < count; i++)
            {
                string name = null;
                try
                {
                    var type = ShaderUtil.GetPropertyType(shader, i);
                    if (type == ShaderUtil.ShaderPropertyType.Float || type == ShaderUtil.ShaderPropertyType.Range)
                        name = ShaderUtil.GetPropertyName(shader, i);
                }
                catch { }
                if (name != null) yield return name;
            }
        }

        static object ConvertValue(Type t, object json, List<string> log)
        {
            if (t == null || json == null) return null;

            try
            {
                if (t == typeof(string)) return json as string ?? Convert.ToString(json, CultureInfo.InvariantCulture);
                if (t == typeof(bool)) return json is bool ? (bool)json : (ToFloat(json) != 0f);
                if (t == typeof(float)) return ToFloat(json);
                if (t == typeof(double)) return (double)ToFloat(json);
                if (t == typeof(int)) return ToInt(json);
                if (t == typeof(uint)) return (uint)Math.Max(0, ToInt(json));
                if (t == typeof(long)) return (long)ToInt(json);

                if (t.IsEnum)
                {
                    var s = json as string;
                    if (s != null) return Enum.Parse(t, s, true);
                    return Enum.ToObject(t, ToInt(json));
                }

                if (t == typeof(Vector2)) { var f = Numbers(json, 2); return f == null ? null : (object)new Vector2(f[0], f[1]); }
                if (t == typeof(Vector3)) { var f = Numbers(json, 3); return f == null ? null : (object)new Vector3(f[0], f[1], f[2]); }
                if (t == typeof(Vector4)) { var f = Numbers(json, 4); return f == null ? null : (object)new Vector4(f[0], f[1], f[2], f[3]); }

                if (t == typeof(Color))
                {
                    var f = Numbers(json, 4);
                    if (f != null) return new Color(f[0], f[1], f[2], f[3]);
                    f = Numbers(json, 3);
                    if (f != null) return new Color(f[0], f[1], f[2], 1f);
                    var hex = json as string;
                    Color parsed;
                    if (hex != null && ColorUtility.TryParseHtmlString(hex, out parsed)) return parsed;
                    return null;
                }

                if (t == typeof(Gradient)) return BuildGradient(json, log);
                if (t == typeof(AnimationCurve)) return BuildCurve(json, log);

                if (typeof(UnityObject).IsAssignableFrom(t))
                {
                    var assetPath = json as string;
                    if (string.IsNullOrEmpty(assetPath)) return null;
                    var asset = AssetDatabase.LoadAssetAtPath(assetPath, t);
                    if (asset == null) log.Add("asset not found at '" + assetPath + "' (expected " + t.Name + ")");
                    return asset;
                }

                // VFX struct types (Position, Sphere, ...) are handled by the caller via children
                if (t.IsValueType && !t.IsPrimitive) return null;

                return Convert.ChangeType(json, t, CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                log.Add("value conversion to " + t.Name + " failed: " + e.Message);
                return null;
            }
        }

        static Gradient BuildGradient(object json, List<string> log)
        {
            var o = JsonReader.AsObject(json);
            if (o == null) return null;
            var g = new Gradient();

            var colorKeys = new List<GradientColorKey>();
            var colors = JsonReader.GetArray(o, "colorKeys");
            if (colors != null)
            {
                foreach (var raw in colors)
                {
                    var k = JsonReader.AsObject(raw);
                    if (k == null) continue;
                    object c;
                    if (!k.TryGetValue("color", out c)) continue;
                    var f = Numbers(c, 3);
                    if (f == null) continue;
                    colorKeys.Add(new GradientColorKey(new Color(f[0], f[1], f[2]), JsonReader.GetFloat(k, "time")));
                }
            }

            var alphaKeys = new List<GradientAlphaKey>();
            var alphas = JsonReader.GetArray(o, "alphaKeys");
            if (alphas != null)
            {
                foreach (var raw in alphas)
                {
                    var k = JsonReader.AsObject(raw);
                    if (k == null) continue;
                    alphaKeys.Add(new GradientAlphaKey(JsonReader.GetFloat(k, "alpha", 1f), JsonReader.GetFloat(k, "time")));
                }
            }

            if (colorKeys.Count == 0) colorKeys.Add(new GradientColorKey(Color.white, 0f));
            if (alphaKeys.Count == 0) { alphaKeys.Add(new GradientAlphaKey(1f, 0f)); alphaKeys.Add(new GradientAlphaKey(0f, 1f)); }

            g.SetKeys(colorKeys.ToArray(), alphaKeys.ToArray());
            return g;
        }

        static AnimationCurve BuildCurve(object json, List<string> log)
        {
            var o = JsonReader.AsObject(json);
            var keys = o != null ? JsonReader.GetArray(o, "keys") : JsonReader.AsArray(json);
            if (keys == null) return null;
            var curve = new AnimationCurve();
            foreach (var raw in keys)
            {
                var k = JsonReader.AsObject(raw);
                if (k != null)
                {
                    curve.AddKey(JsonReader.GetFloat(k, "time"), JsonReader.GetFloat(k, "value"));
                    continue;
                }
                var pair = JsonReader.AsArray(raw);
                if (pair != null && pair.Count >= 2) curve.AddKey(ToFloat(pair[0]), ToFloat(pair[1]));
            }
            return curve.length > 0 ? curve : null;
        }

        // ============================================================== links

        static void ApplyLink(Dictionary<string, object> spec, Dictionary<string, VFXModel> byId, List<string> log)
        {
            if (spec == null) return;
            var from = ResolveSlotRef(byId, JsonReader.GetString(spec, "from"), JsonReader.GetString(spec, "fromSlot"), false, log);
            var to = ResolveSlotRef(byId, JsonReader.GetString(spec, "to"), JsonReader.GetString(spec, "toSlot"), true, log);
            if (from == null || to == null) return;

            if (!to.CanLink(from) || !from.CanLink(to))
            {
                log.Add("REJECTED link " + from.path + " -> " + to.path + ": Unity says those slot types are not compatible");
                return;
            }

            if (to.Link(from)) log.Add("linked " + JsonReader.GetString(spec, "from") + "." + from.path
                                       + " -> " + JsonReader.GetString(spec, "to") + "." + to.path);
            else log.Add("FAILED link " + from.path + " -> " + to.path);
        }

        static VFXSlot ResolveSlotRef(Dictionary<string, VFXModel> byId, string nodeId, string slotPath, bool input, List<string> log)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                log.Add("SKIPPED link: missing node id");
                return null;
            }
            VFXModel model;
            if (!byId.TryGetValue(nodeId, out model))
            {
                log.Add("SKIPPED link: no node with id '" + nodeId + "'");
                return null;
            }
            var container = model as IVFXSlotContainer;
            if (container == null)
            {
                log.Add("SKIPPED link: node '" + nodeId + "' has no slots");
                return null;
            }

            var slots = input ? container.inputSlots : container.outputSlots;
            if (slots.Count == 0)
            {
                log.Add("SKIPPED link: node '" + nodeId + "' has no " + (input ? "input" : "output") + " slots");
                return null;
            }

            if (string.IsNullOrEmpty(slotPath)) return slots[0];

            var slot = FindSlot(slots, slotPath);
            if (slot == null)
            {
                var paths = AllSlotPaths(slots).Take(16).ToArray();
                log.Add("SKIPPED link: '" + nodeId + "' has no " + (input ? "input" : "output")
                        + " slot '" + slotPath + "' - options: " + string.Join(", ", paths));
            }
            return slot;
        }

        static void LinkContexts(Dictionary<string, VFXModel> byId, string fromId, string toId, int fromIndex, int toIndex, List<string> log)
        {
            VFXModel a, b;
            if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId)) { log.Add("SKIPPED flow link: missing ids"); return; }
            if (!byId.TryGetValue(fromId, out a) || !byId.TryGetValue(toId, out b))
            {
                log.Add("SKIPPED flow link " + fromId + " -> " + toId + ": unknown id");
                return;
            }
            TryLink(a as VFXContext, b as VFXContext, fromIndex, toIndex, log);
        }

        static void TryLink(VFXContext from, VFXContext to, int fromIndex, int toIndex, List<string> log)
        {
            if (from == null || to == null) { log.Add("SKIPPED flow link: not a context"); return; }
            if (!VFXContext.CanLink(from, to, fromIndex, toIndex))
            {
                log.Add("REJECTED flow link " + DisplayName(from) + " -> " + DisplayName(to)
                        + ": Unity says those contexts cannot connect");
                return;
            }
            from.LinkTo(to, fromIndex, toIndex);
            log.Add("flow " + DisplayName(from) + " -> " + DisplayName(to));
        }

        // ============================================================== slot helpers

        static VFXSlot FindSlot(IEnumerable<VFXSlot> roots, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var list = roots as IList<VFXSlot> ?? roots.ToList();

            // exact first, so a deliberate path always beats a fuzzy name match
            foreach (var root in list)
            {
                var hit = FindSlotRecursive(root, path, 0, false);
                if (hit != null) return hit;
            }

            // then normalized: slot names are prefixed in the model ("_Lifetime", "_Position")
            // while everyone writes them without the underscore.
            foreach (var root in list)
            {
                var hit = FindSlotRecursive(root, path, 0, true);
                if (hit != null) return hit;
            }
            return null;
        }

        static VFXSlot FindSlotRecursive(VFXSlot slot, string path, int depth, bool normalized)
        {
            if (slot == null || depth > 6) return null;
            string p = null, n = null;
            try { p = slot.path; n = slot.name; } catch { }

            if (!normalized)
            {
                if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return slot;
                if (string.Equals(n, path, StringComparison.OrdinalIgnoreCase)) return slot;
            }
            else
            {
                var q = Normalize(path);
                if (Normalize(p) == q || Normalize(n) == q) return slot;
            }

            List<VFXSlot> kids = null;
            try { kids = slot.children.ToList(); } catch { }
            if (kids == null) return null;
            foreach (var k in kids)
            {
                var hit = FindSlotRecursive(k, path, depth + 1, normalized);
                if (hit != null) return hit;
            }
            return null;
        }

        static IEnumerable<string> AllSlotPaths(IEnumerable<VFXSlot> roots)
        {
            foreach (var r in roots)
                foreach (var p in SlotPaths(r, 0))
                    yield return p;
        }

        static IEnumerable<string> SlotPaths(VFXSlot slot, int depth)
        {
            if (slot == null || depth > 4) yield break;
            string p = null;
            try { p = slot.path; } catch { }
            if (!string.IsNullOrEmpty(p)) yield return p;

            List<VFXSlot> kids = null;
            try { kids = slot.children.ToList(); } catch { }
            if (kids == null) yield break;
            foreach (var k in kids)
                foreach (var q in SlotPaths(k, depth + 1))
                    yield return q;
        }

        // ============================================================== inspection helpers

        static List<KeyValuePair<string, VFXModel>> BuildIdMap(VFXGraph graph)
        {
            var list = new List<KeyValuePair<string, VFXModel>>();
            int ci = 0, oi = 0, pi = 0, xi = 0;

            foreach (var child in graph.children)
            {
                var ctx = child as VFXContext;
                if (ctx != null)
                {
                    var cid = "c" + ci++;
                    list.Add(new KeyValuePair<string, VFXModel>(cid, ctx));
                    int bi = 0;
                    foreach (var block in ctx.children)
                        list.Add(new KeyValuePair<string, VFXModel>(cid + ".b" + bi++, block));
                    continue;
                }
                if (child is VFXOperator) { list.Add(new KeyValuePair<string, VFXModel>("o" + oi++, child)); continue; }
                if (child is VFXParameter) { list.Add(new KeyValuePair<string, VFXModel>("p" + pi++, child)); continue; }
                list.Add(new KeyValuePair<string, VFXModel>("x" + xi++, child));
            }
            return list;
        }

        static bool TryGetId(List<KeyValuePair<string, VFXModel>> ids, VFXModel model, out string id)
        {
            foreach (var kv in ids)
            {
                if (ReferenceEquals(kv.Value, model)) { id = kv.Key; return true; }
            }
            id = null;
            return false;
        }

        static void WriteSettings(JsonWriter w, VFXModel model)
        {
            w.BeginObject("settings");
            try
            {
                foreach (var s in model.GetSettings(true))
                {
                    if (!s.valid) continue;
                    w.Prop(s.name, Try(() => s.value == null ? null : VfxAiCatalog.Stringify(s.value)));
                }
            }
            catch { }
            w.EndObject();
        }

        /// <summary>
        /// Sticky notes and group boxes. In Unity's own sample graphs these hold the actual
        /// explanation of why a graph is built the way it is - design intent that exists nowhere
        /// in the node data, and which is usually more valuable than the structure itself.
        /// </summary>
        static void WriteAnnotations(JsonWriter w, VFXGraph graph, List<KeyValuePair<string, VFXModel>> ids)
        {
            VFXUI ui = null;
            try { ui = graph.UIInfos; } catch { }

            w.BeginArray("stickyNotes");
            if (ui != null && ui.stickyNoteInfos != null)
            {
                foreach (var note in ui.stickyNoteInfos)
                {
                    if (note == null) continue;
                    w.BeginObject();
                    w.Prop("title", note.title);
                    w.Prop("contents", note.contents);
                    try
                    {
                        w.Key("position").BeginArray()
                            .Value(note.position.x).Value(note.position.y)
                            .Value(note.position.width).Value(note.position.height)
                            .EndArray();
                    }
                    catch { }
                    w.Prop("nearestNode", NearestNodeId(ids, note.position));
                    w.EndObject();
                }
            }
            w.EndArray();

            w.BeginArray("groups");
            if (ui != null && ui.groupInfos != null)
            {
                for (int g = 0; g < ui.groupInfos.Length; g++)
                {
                    var group = ui.groupInfos[g];
                    if (group == null) continue;
                    w.BeginObject();
                    w.Prop("title", group.title);
                    w.BeginArray("members");
                    if (group.contents != null)
                    {
                        foreach (var nodeId in group.contents)
                        {
                            if (nodeId.isStickyNote) { w.Value("stickyNote:" + nodeId.id); continue; }
                            string id;
                            if (nodeId.model != null && TryGetId(ids, nodeId.model, out id)) w.Value(id);
                        }
                    }
                    w.EndArray();
                    w.EndObject();
                }
            }
            w.EndArray();
        }

        /// <summary>
        /// Sticky notes are positioned, not attached, so the only link back to what a note is
        /// describing is proximity. Good enough to pair prose with the nodes it explains.
        /// </summary>
        static string NearestNodeId(List<KeyValuePair<string, VFXModel>> ids, Rect noteRect)
        {
            string best = null;
            float bestDistance = float.MaxValue;
            var noteCenter = new Vector2(noteRect.x + noteRect.width * 0.5f, noteRect.y + noteRect.height * 0.5f);

            foreach (var kv in ids)
            {
                if (kv.Value is VFXBlock) continue; // blocks sit inside contexts, too fine-grained
                Vector2 p;
                try { p = kv.Value.position; } catch { continue; }
                var d = Vector2.Distance(p, noteCenter);
                if (d < bestDistance) { bestDistance = d; best = kv.Key; }
            }
            return bestDistance < 900f ? best : null;
        }

        /// <summary>Rendered outputs also carry material overrides that are invisible to GetSettings.</summary>
        static void WriteMaterial(JsonWriter w, VFXModel model)
        {
            var output = model as VFXAbstractRenderedOutput;
            if (output == null) return;

            Material material = null;
            try { material = output.FindMaterial(); } catch { }
            if (material == null) return;

            w.BeginObject("materialProperties");
            try
            {
                foreach (var name in FloatPropertyNames(material))
                    w.Prop(name, material.GetFloat(name));
            }
            catch { }
            w.EndObject();
        }

        static void WriteSlots(JsonWriter w, VFXModel model, List<KeyValuePair<string, VFXModel>> ids)
        {
            var container = model as IVFXSlotContainer;
            if (container == null) return;

            w.BeginArray("inputs");
            try
            {
                foreach (var root in container.inputSlots)
                    foreach (var slot in Flatten(root, 0))
                    {
                        w.BeginObject();
                        w.Prop("path", Try(() => slot.path));
                        w.Prop("type", Try(() => slot.property.type != null ? slot.property.type.Name : null));
                        w.Prop("value", Try(() => VfxAiCatalog.Stringify(slot.value)));
                        var linked = new List<string>();
                        try
                        {
                            foreach (var other in slot.LinkedSlots)
                            {
                                var owner = other.owner as VFXModel;
                                string oid;
                                if (owner != null && TryGetId(ids, owner, out oid))
                                    linked.Add(oid + ":" + other.path);
                            }
                        }
                        catch { }
                        if (linked.Count > 0)
                        {
                            w.BeginArray("linkedFrom");
                            foreach (var l in linked) w.Value(l);
                            w.EndArray();
                        }
                        w.EndObject();
                    }
            }
            catch { }
            w.EndArray();

            w.BeginArray("outputs");
            try
            {
                foreach (var root in container.outputSlots)
                    foreach (var slot in Flatten(root, 0))
                    {
                        w.BeginObject();
                        w.Prop("path", Try(() => slot.path));
                        w.Prop("type", Try(() => slot.property.type != null ? slot.property.type.Name : null));
                        w.EndObject();
                    }
            }
            catch { }
            w.EndArray();
        }

        static IEnumerable<VFXSlot> Flatten(VFXSlot slot, int depth)
        {
            if (slot == null || depth > 3) yield break;
            yield return slot;
            List<VFXSlot> kids = null;
            try { kids = slot.children.ToList(); } catch { }
            if (kids == null) yield break;
            foreach (var k in kids)
                foreach (var s in Flatten(k, depth + 1))
                    yield return s;
        }

        // ============================================================== misc

        static string KindOf(VFXModel m)
        {
            if (m is VFXContext) return "context";
            if (m is VFXBlock) return "block";
            if (m is VFXOperator) return "operator";
            if (m is VFXParameter) return "parameter";
            return m.GetType().Name;
        }

        static string DisplayName(VFXModel m)
        {
            if (m == null) return "<null>";
            try
            {
                var n = m.name;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            return m.GetType().Name;
        }

        static string Try(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }

        static float ToFloat(object o)
        {
            if (o is double) return (float)(double)o;
            if (o is float) return (float)o;
            if (o is int) return (int)o;
            if (o is bool) return (bool)o ? 1f : 0f;
            float f;
            return float.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : 0f;
        }

        static int ToInt(object o)
        {
            if (o is double) return (int)Math.Round((double)o);
            if (o is int) return (int)o;
            if (o is bool) return (bool)o ? 1 : 0;
            int i;
            return int.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : 0;
        }

        static float[] Numbers(object json, int count)
        {
            var arr = JsonReader.AsArray(json);
            if (arr != null)
            {
                if (arr.Count != count) return null;
                var f = new float[count];
                for (int i = 0; i < count; i++) f[i] = ToFloat(arr[i]);
                return f;
            }
            if (count == 1) return new[] { ToFloat(json) };

            var obj = JsonReader.AsObject(json);
            if (obj != null)
            {
                var keys = count == 4 ? new[] { "x", "y", "z", "w" } : new[] { "x", "y", "z" };
                if (obj.ContainsKey("r")) keys = count == 4 ? new[] { "r", "g", "b", "a" } : new[] { "r", "g", "b" };
                var f = new float[count];
                for (int i = 0; i < count; i++)
                {
                    if (i >= keys.Length || !obj.ContainsKey(keys[i])) return null;
                    f[i] = JsonReader.GetFloat(obj, keys[i]);
                }
                return f;
            }

            // a single number broadcast to every component
            if (json is double || json is int || json is float)
            {
                var v = ToFloat(json);
                var f = new float[count];
                for (int i = 0; i < count; i++) f[i] = v;
                return f;
            }
            return null;
        }

        static JsonWriter OkHeader()
        {
            var w = new JsonWriter(true);
            w.BeginObject();
            w.Prop("status", "ok");
            return w;
        }

        static string Fail(string message)
        {
            return VfxAiKernelApi.Error(message);
        }
    }
}
