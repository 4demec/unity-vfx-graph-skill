// VfxAiCatalog.cs
// Dumps the Visual Effect Graph node library of THIS editor install to disk, so the AI side
// can address nodes by verified name/category/type instead of guessing.
//
// Compiled into Unity.VisualEffectGraph.Editor via VFXAI.Kernel.asmref (needed: every VFX
// authoring type in UnityEditor.VFX is internal to that assembly).
//
// Menu:  Tools > VFX AI > Dump Node Catalog
// Batch: -executeMethod VfxAi.Kernel.VfxAiCatalog.DumpForBatch
//
// Output (project root, outside Assets so it never triggers a reimport):
//   VFXAI_Reports/catalog.jsonl        one JSON object per node descriptor
//   VFXAI_Reports/catalog_index.tsv    compact kind/category/name/type index
//   VFXAI_Reports/slot_types.tsv       property types usable on slots
//   VFXAI_Reports/kernel_status.json   version + environment stamp

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.VFX;
using UnityEngine;

namespace VfxAi.Kernel
{
    public static class VfxAiCatalog
    {
        public const string kKernelVersion = "0.4.0";
        const int kMaxSlotDepth = 4;

        public static string outputDirectory
        {
            get
            {
                var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "VFXAI_Reports"));
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        [MenuItem("Tools/VFX AI/Dump Node Catalog")]
        public static void DumpMenu()
        {
            var dir = Dump();
            Debug.Log("[VFX AI] node catalog written to " + dir);
            EditorUtility.RevealInFinder(dir);
        }

        public static void DumpForBatch()
        {
            var dir = Dump();
            Console.WriteLine("[VFX AI] node catalog written to " + dir);
        }

        public static string Dump()
        {
            var dir = outputDirectory;
            int contexts = 0, blocks = 0, operators = 0, parameters = 0;

            var indexLines = new List<string> { "kind\tcategory\tname\tmodelType\tvariantOf" };

            using (var w = new StreamWriter(Path.Combine(dir, "catalog.jsonl"), false))
            {
                foreach (var d in Safe(() => VFXLibrary.GetContexts().Cast<IVFXModelDescriptor>()))
                    contexts += Emit(w, indexLines, "context", d, null, 0);

                foreach (var d in Safe(() => VFXLibrary.GetBlocks().Cast<IVFXModelDescriptor>()))
                    blocks += Emit(w, indexLines, "block", d, null, 0);

                foreach (var d in Safe(() => VFXLibrary.GetOperators().Cast<IVFXModelDescriptor>()))
                    operators += Emit(w, indexLines, "operator", d, null, 0);

                foreach (var d in Safe(() => VFXLibrary.GetParameters().Cast<IVFXModelDescriptor>()))
                    parameters += Emit(w, indexLines, "parameter", d, null, 0);
            }

            File.WriteAllLines(Path.Combine(dir, "catalog_index.tsv"), indexLines);

            // Slot / property types that can be used for exposed parameters and slot values.
            var typeLines = new List<string> { "typeFullName\ttypeName\tspaceable" };
            foreach (var t in Safe(() => VFXLibrary.GetSlotsType()))
            {
                bool spaceable = false;
                try { spaceable = VFXLibrary.IsSpaceableSlotType(t); } catch { }
                typeLines.Add(string.Join("\t", new[] { t.FullName, t.Name, spaceable.ToString() }));
            }
            File.WriteAllLines(Path.Combine(dir, "slot_types.tsv"), typeLines);

            var status = new JsonWriter(true);
            status.BeginObject();
            status.Prop("kernelVersion", kKernelVersion);
            status.Prop("unityVersion", Application.unityVersion);
            status.Prop("timestampUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            status.Prop("contexts", contexts);
            status.Prop("blocks", blocks);
            status.Prop("operators", operators);
            status.Prop("parameters", parameters);
            status.Prop("srpBinder", SafeString(() => VFXLibrary.currentSRPBinder != null ? VFXLibrary.currentSRPBinder.GetType().FullName : "none"));
            status.EndObject();
            File.WriteAllText(Path.Combine(dir, "kernel_status.json"), status.ToString());

            return dir;
        }

        // ---------------------------------------------------------------- helpers

        static List<T> Safe<T>(Func<IEnumerable<T>> f)
        {
            var list = new List<T>();
            try
            {
                var result = f();
                if (result != null) list.AddRange(result);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VFX AI] catalog query failed: " + e.Message);
            }
            return list;
        }

        static string SafeString(Func<string> f)
        {
            try { return f(); } catch (Exception e) { return "error: " + e.Message; }
        }

        static int Emit(StreamWriter w, List<string> index, string kind, IVFXModelDescriptor d, string variantOf, int depth)
        {
            if (d == null || depth > 3) return 0;
            int count = 0;

            string name = "", category = "", modelType = "";
            try { name = d.name ?? ""; } catch { }
            try { category = d.category ?? ""; } catch { }
            try { modelType = d.modelType != null ? d.modelType.FullName : ""; } catch { }

            var j = new JsonWriter();
            j.BeginObject();
            j.Prop("kind", kind);
            j.Prop("name", name);
            j.Prop("category", category);
            j.Prop("modelType", modelType);
            if (variantOf != null) j.Prop("variantOf", variantOf);

            j.BeginArray("synonyms");
            try { foreach (var s in d.synonyms ?? new string[0]) j.Value(s); } catch { }
            j.EndArray();

            j.BeginObject("variantSettings");
            try
            {
                var settings = d.variant != null ? d.variant.settings : null;
                if (settings != null)
                    foreach (var kv in settings)
                        j.Prop(kv.Key, kv.Value == null ? null : kv.Value.ToString());
            }
            catch { }
            j.EndObject();

            VFXModel model = null;
            try { model = d.unTypedModel; }
            catch (Exception e) { j.Prop("modelError", e.Message); }

            if (model != null)
            {
                try { DescribeModel(j, model); }
                catch (Exception e) { j.Prop("describeError", e.Message); }
            }

            j.EndObject();
            w.WriteLine(j.ToString());
            index.Add(string.Join("\t", new[] { kind, category, name, modelType, variantOf ?? "" }));
            count++;

            IVFXModelDescriptor[] subs = null;
            try { subs = d.subVariantDescriptors; } catch { }
            if (subs != null)
            {
                var parentId = category + "/" + name;
                foreach (var sub in subs)
                    count += Emit(w, index, kind, sub, parentId, depth + 1);
            }

            return count;
        }

        static void DescribeModel(JsonWriter j, VFXModel model)
        {
            j.Prop("modelName", SafeString(() => model.name));

            j.BeginArray("settings");
            IEnumerable<VFXSetting> settings = Enumerable.Empty<VFXSetting>();
            try { settings = model.GetSettings(true).ToList(); } catch { }
            foreach (var s in settings)
            {
                if (!s.valid) continue;
                j.BeginObject();
                j.Prop("name", s.name);
                var ft = s.field != null ? s.field.FieldType : null;
                j.Prop("type", ft != null ? ft.FullName : null);
                j.Prop("value", SafeString(() => s.value == null ? null : Stringify(s.value)));
                j.Prop("visibility", s.visibility.ToString());
                if (ft != null && ft.IsEnum)
                {
                    j.BeginArray("enumValues");
                    foreach (var n in Enum.GetNames(ft)) j.Value(n);
                    j.EndArray();
                }
                j.EndObject();
            }
            j.EndArray();

            var container = model as IVFXSlotContainer;
            if (container != null)
            {
                j.BeginArray("inputSlots");
                try { foreach (var s in container.inputSlots) EmitSlot(j, s, 0); } catch { }
                j.EndArray();

                j.BeginArray("outputSlots");
                try { foreach (var s in container.outputSlots) EmitSlot(j, s, 0); } catch { }
                j.EndArray();
            }

            var ctx = model as VFXContext;
            if (ctx != null)
            {
                j.Prop("contextType", SafeString(() => ctx.contextType.ToString()));
                j.Prop("inputDataType", SafeString(() => ctx.inputType.ToString()));
                j.Prop("outputDataType", SafeString(() => ctx.outputType.ToString()));
                j.Prop("taskType", SafeString(() => ctx.taskType.ToString()));
                try { j.Prop("inputFlowCount", ctx.inputFlowSlot.Length); } catch { }
                try { j.Prop("outputFlowCount", ctx.outputFlowSlot.Length); } catch { }
                try { j.Prop("canHaveBlocks", ctx.CanHaveBlocks()); } catch { }
            }

            var blk = model as VFXBlock;
            if (blk != null)
            {
                j.Prop("compatibleContexts", SafeString(() => blk.compatibleContexts.ToString()));
                j.Prop("compatibleData", SafeString(() => blk.compatibleData.ToString()));
                j.BeginArray("attributes");
                try
                {
                    foreach (var a in blk.attributes)
                        j.Value(a.attrib.name + ":" + a.mode);
                }
                catch { }
                j.EndArray();
            }
        }

        static void EmitSlot(JsonWriter j, VFXSlot s, int depth)
        {
            if (s == null) return;
            j.BeginObject();
            j.Prop("name", SafeString(() => s.name));
            j.Prop("path", SafeString(() => s.path));
            Type t = null;
            try { t = s.property.type; } catch { }
            j.Prop("type", t != null ? t.FullName : null);
            j.Prop("valueType", SafeString(() => s.valueType.ToString()));
            j.Prop("default", SafeString(() => { var v = s.value; return v == null ? null : Stringify(v); }));

            if (depth < kMaxSlotDepth)
            {
                List<VFXSlot> kids = null;
                try { kids = s.children.ToList(); } catch { }
                if (kids != null && kids.Count > 0)
                {
                    j.BeginArray("children");
                    foreach (var c in kids) EmitSlot(j, c, depth + 1);
                    j.EndArray();
                }
            }
            j.EndObject();
        }

        internal static string Stringify(object v)
        {
            if (v == null) return null;
            if (v is float) return ((float)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is Vector2) { var x = (Vector2)v; return F(x.x) + "," + F(x.y); }
            if (v is Vector3) { var x = (Vector3)v; return F(x.x) + "," + F(x.y) + "," + F(x.z); }
            if (v is Vector4) { var x = (Vector4)v; return F(x.x) + "," + F(x.y) + "," + F(x.z) + "," + F(x.w); }
            if (v is Color) { var x = (Color)v; return F(x.r) + "," + F(x.g) + "," + F(x.b) + "," + F(x.a); }
            if (v is UnityEngine.Object)
            {
                var o = (UnityEngine.Object)v;
                return o == null ? null : (o.name + " (" + o.GetType().Name + ")");
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static string F(float f) { return f.ToString("R", CultureInfo.InvariantCulture); }
    }
}
