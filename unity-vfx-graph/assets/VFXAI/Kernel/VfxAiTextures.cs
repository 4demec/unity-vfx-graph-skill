// VfxAiTextures.cs
// Texture-side ops for the VFX AI kernel: find what the project has, fix how it is imported,
// and build flipbook sheets out of loose frame sequences.
//
// None of this needs VFX internals - it is plain UnityEditor API - but it lives in the kernel
// assembly so it can share the JSON helpers and the single Invoke entry point.
//
//   textures   read-only   scan Texture2D assets, report size / alpha / flipbook grid / issues
//   texconfig  MUTATES     apply importer settings (presets: particle, flipbook)
//   flipbook   MUTATES     assemble a folder of PNG frames into one sheet, named <name>_CxR.png
//
// Everything a VFX output cares about that the file name cannot tell you - whether the source
// actually has an alpha channel, whether mipmaps will smear neighbouring flipbook frames - comes
// from the importer, so ask it rather than guessing from the texture's looks.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace VfxAi.Kernel
{
    public static class VfxAiTextures
    {
        public const string kVersion = "0.1.0";

        const int kDefaultLimit = 400;
        const int kMaxSheetSize = 4096;

        /// <summary>Trailing "_8x8" / "_16x4" in a file name: the flipbook grid, columns x rows.</summary>
        static readonly Regex kGridInName = new Regex(@"_(\d{1,3})x(\d{1,3})(?![0-9])", RegexOptions.Compiled);

        // ================================================================== textures (scan)

        public static string Scan(Dictionary<string, object> args)
        {
            string folderError;
            var folders = ResolveFolders(args, out folderError);
            if (folderError != null) return VfxAiKernelApi.Error(folderError);

            var filter = (JsonReader.GetString(args, "filter", null) ?? "").ToLowerInvariant();
            var limit = JsonReader.GetInt(args, "limit", kDefaultLimit);
            var wantReport = JsonReader.GetBool(args, "report", true);

            string[] guids;
            try { guids = AssetDatabase.FindAssets("t:Texture2D", folders); }
            catch (Exception e) { return VfxAiKernelApi.Error("texture search failed: " + e.Message); }

            var paths = new List<string>();
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(p)) continue;
                if (filter.Length > 0 && p.ToLowerInvariant().IndexOf(filter, StringComparison.Ordinal) < 0) continue;
                paths.Add(p);
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);

            var total = paths.Count;
            var truncated = total > limit;
            if (truncated) paths.RemoveRange(limit, total - limit);

            var rows = new List<string> { "path\twidth\theight\tcols\trows\thasAlpha\talphaIsTransparency\tmipmaps\tissues" };

            var w = new JsonWriter(true);
            w.BeginObject();
            w.Prop("status", "ok");
            w.Prop("scanned", total);
            w.Prop("returned", paths.Count);
            if (truncated) w.Prop("note", "result truncated by 'limit'; narrow with 'folder'/'filter' or raise it");
            w.BeginArray("textures");

            foreach (var path in paths)
            {
                var info = Describe(path);
                if (info == null) continue;

                w.BeginObject();
                w.Prop("path", info.path);
                w.Prop("name", info.name);
                w.Prop("width", info.width);
                w.Prop("height", info.height);
                w.Prop("hasAlpha", info.hasAlpha);
                w.Prop("alphaIsTransparency", info.alphaIsTransparency);
                w.Prop("mipmaps", info.mipmaps);
                w.Prop("sRGB", info.sRGB);
                w.Prop("wrapMode", info.wrapMode);
                w.Prop("textureType", info.textureType);
                w.Prop("maxTextureSize", info.maxTextureSize);
                if (info.columns > 0)
                {
                    w.BeginObject("flipbook");
                    w.Prop("columns", info.columns);
                    w.Prop("rows", info.rows);
                    w.Prop("frames", info.columns * info.rows);
                    w.Prop("frameWidth", info.width / Math.Max(1, info.columns));
                    w.Prop("frameHeight", info.height / Math.Max(1, info.rows));
                    w.EndObject();
                }
                w.BeginArray("issues");
                foreach (var issue in info.issues) w.Value(issue);
                w.EndArray();
                w.EndObject();

                rows.Add(string.Join("\t", new[]
                {
                    info.path,
                    info.width.ToString(CultureInfo.InvariantCulture),
                    info.height.ToString(CultureInfo.InvariantCulture),
                    info.columns > 0 ? info.columns.ToString(CultureInfo.InvariantCulture) : "",
                    info.rows > 0 ? info.rows.ToString(CultureInfo.InvariantCulture) : "",
                    info.hasAlpha ? "1" : "0",
                    info.alphaIsTransparency ? "1" : "0",
                    info.mipmaps ? "1" : "0",
                    string.Join("; ", info.issues.ToArray())
                }));
            }

            w.EndArray();

            if (wantReport)
            {
                try
                {
                    var file = Path.Combine(VfxAiCatalog.outputDirectory, "texture_index.tsv");
                    File.WriteAllLines(file, rows);
                    w.Prop("report", file);
                }
                catch (Exception e) { w.Prop("reportError", e.Message); }
            }

            w.EndObject();
            return w.ToString();
        }

        class TexInfo
        {
            public string path, name, wrapMode, textureType;
            public int width, height, columns, rows, maxTextureSize;
            public bool hasAlpha, alphaIsTransparency, mipmaps, sRGB;
            public readonly List<string> issues = new List<string>();
        }

        static TexInfo Describe(string path)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return null;

            var info = new TexInfo
            {
                path = path,
                name = Path.GetFileNameWithoutExtension(path),
                alphaIsTransparency = ti.alphaIsTransparency,
                mipmaps = ti.mipmapEnabled,
                sRGB = ti.sRGBTexture,
                wrapMode = ti.wrapMode.ToString(),
                textureType = ti.textureType.ToString(),
                maxTextureSize = ti.maxTextureSize,
            };

            try { info.hasAlpha = ti.DoesSourceTextureHaveAlpha(); }
            catch { info.hasAlpha = true; }

            int w, h;
            SourceSize(ti, path, out w, out h);
            info.width = w;
            info.height = h;

            var m = kGridInName.Match(info.name);
            if (m.Success)
            {
                // Last match wins: "impact_white_6x4" and "fire_01_8x8" both end on the grid.
                while (true)
                {
                    var next = m.NextMatch();
                    if (!next.Success) break;
                    m = next;
                }
                info.columns = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                info.rows = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }

            if (!info.hasAlpha)
                info.issues.Add("no alpha channel - luminance sheet: use blendMode Additive, "
                                + "Alpha blending renders the black background as a black card");
            else if (!info.alphaIsTransparency)
                info.issues.Add("alphaIsTransparency off - colour in fully transparent pixels is undefined, "
                                + "so filtering pulls dark fringes into soft edges");

            if (info.columns > 0)
            {
                if (info.mipmaps)
                    info.issues.Add("mipmaps on a flipbook sheet - lower mips blend neighbouring frames "
                                    + "into each other at distance; turn them off");
                if (info.width > 0 && info.columns > 0 && info.width % info.columns != 0)
                    info.issues.Add("width " + info.width + " does not divide by " + info.columns
                                    + " columns - frame edges land mid-texel");
                if (info.height > 0 && info.rows > 0 && info.height % info.rows != 0)
                    info.issues.Add("height " + info.height + " does not divide by " + info.rows + " rows");
            }

            if (ti.wrapMode != TextureWrapMode.Clamp)
                info.issues.Add("wrap mode " + ti.wrapMode + " - edge texels wrap onto the opposite side; use Clamp");

            if (ti.textureType != TextureImporterType.Default)
                info.issues.Add("texture type " + ti.textureType + " - VFX outputs want Default");

            if (info.width > 0 && info.height > 0 && !(IsPot(info.width) && IsPot(info.height))
                && ti.npotScale != TextureImporterNPOTScale.None)
                info.issues.Add("non-power-of-two source with npotScale=" + ti.npotScale
                                + " - Unity rescales the image on import, changing frame aspect");

            return info;
        }

        static bool IsPot(int v) { return v > 0 && (v & (v - 1)) == 0; }

        static MethodInfo s_SizeMethod;
        static bool s_SizeSearched;

        /// <summary>
        /// Source pixel size without loading the texture. TextureImporter knows it, but only
        /// through an internal method, so reach it by reflection and fall back to a real load.
        /// </summary>
        static void SourceSize(TextureImporter ti, string path, out int width, out int height)
        {
            width = height = 0;

            if (!s_SizeSearched)
            {
                s_SizeSearched = true;
                foreach (var n in new[] { "GetWidthAndHeight", "GetSourceTextureWidthAndHeight" })
                {
                    s_SizeMethod = typeof(TextureImporter).GetMethod(n,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (s_SizeMethod != null) break;
                }
            }

            if (s_SizeMethod != null)
            {
                try
                {
                    var p = new object[] { 0, 0 };
                    s_SizeMethod.Invoke(ti, p);
                    width = Convert.ToInt32(p[0], CultureInfo.InvariantCulture);
                    height = Convert.ToInt32(p[1], CultureInfo.InvariantCulture);
                }
                catch { width = height = 0; }
            }

            if (width > 0 && height > 0) return;

            var tex = AssetDatabase.LoadAssetAtPath<Texture>(path);
            if (tex == null) return;
            width = tex.width;
            height = tex.height;
        }

        // ================================================================== texconfig

        public static string Configure(Dictionary<string, object> args)
        {
            string selectError;
            var targets = ResolveTargets(args, out selectError);
            if (selectError != null) return VfxAiKernelApi.Error(selectError);
            if (targets.Count == 0)
                return VfxAiKernelApi.Error("no textures matched - pass 'paths', or 'folder' (+ optional 'filter')");

            var preset = (JsonReader.GetString(args, "preset", null) ?? "").ToLowerInvariant();
            var overrides = JsonReader.GetObject(args, "settings");
            var dryRun = JsonReader.GetBool(args, "dryRun", false);

            // Write every matched file even when the importer claims it is already correct. The
            // repair hatch for a stale in-memory importer, and for a .meta edited behind Unity's
            // back - either way the object and the file on disk disagree.
            var force = JsonReader.GetBool(args, "force", false) && !dryRun;

            var settings = new Dictionary<string, object>();
            if (preset == "particle" || preset == "flipbook")
            {
                settings["textureType"] = "Default";
                settings["alphaIsTransparency"] = true;
                settings["wrapMode"] = "Clamp";
                settings["alphaSource"] = "FromInput";
                if (preset == "flipbook")
                {
                    settings["mipmaps"] = false;
                    settings["npotScale"] = "None";
                }
            }
            else if (preset.Length > 0 && preset != "none")
            {
                return VfxAiKernelApi.Error("unknown preset '" + preset + "' - use 'particle', 'flipbook' or 'none'");
            }
            if (overrides != null)
                foreach (var kv in overrides) settings[kv.Key] = kv.Value;

            if (settings.Count == 0)
                return VfxAiKernelApi.Error("nothing to apply - pass a 'preset' and/or a 'settings' object");

            var w = new JsonWriter(true);
            w.BeginObject();
            w.Prop("status", "ok");
            w.Prop("dryRun", dryRun);
            w.Prop("force", force);
            w.Prop("matched", targets.Count);
            w.BeginArray("changed");

            int changedCount = 0, unchanged = 0, failed = 0;
            var log = new List<string>();

            foreach (var path in targets)
            {
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) { failed++; log.Add("SKIPPED " + path + ": not a texture importer"); continue; }

                List<string> diffs;
                try { diffs = ApplySettings(ti, settings, log, path, !dryRun); }
                catch (Exception e) { failed++; log.Add("SKIPPED " + path + ": " + e.Message); continue; }

                if (diffs.Count == 0 && !force) { unchanged++; continue; }

                changedCount++;
                w.BeginObject();
                w.Prop("path", path);
                w.BeginArray("changes");
                foreach (var d in diffs) w.Value(d);
                w.EndArray();
                w.EndObject();

                if (!dryRun)
                {
                    try { ti.SaveAndReimport(); }
                    catch (Exception e) { failed++; log.Add("REIMPORT FAILED " + path + ": " + e.Message); }
                }
            }

            w.EndArray();
            w.Prop("changedCount", changedCount);
            w.Prop("unchangedCount", unchanged);
            w.Prop("failedCount", failed);
            w.BeginArray("log");
            foreach (var l in log) w.Value(l);
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        /// <summary>
        /// Diffs the requested importer fields, returning one "field: old -> new" line each, and
        /// writes them only when <paramref name="apply"/> is set.
        ///
        /// A dry run MUST NOT assign. AssetImporter is a live serialized object: assigning without
        /// SaveAndReimport leaves the instance dirty in memory, so a later real run diffs against
        /// the dry run's phantom values, sees "nothing to change", skips the save - and the file on
        /// disk keeps its original settings while every job reports success.
        /// </summary>
        static List<string> ApplySettings(TextureImporter ti, Dictionary<string, object> settings,
                                          List<string> log, string path, bool apply)
        {
            var diffs = new List<string>();

            foreach (var kv in settings)
            {
                var key = kv.Key;
                var val = kv.Value;

                switch (key.ToLowerInvariant())
                {
                    case "alphaistransparency":
                    {
                        var v = AsBool(val);
                        if (ti.alphaIsTransparency != v) { diffs.Add(Diff(key, ti.alphaIsTransparency, v)); if (apply) ti.alphaIsTransparency = v; }
                        break;
                    }
                    case "mipmaps":
                    case "mipmapenabled":
                    {
                        var v = AsBool(val);
                        if (ti.mipmapEnabled != v) { diffs.Add(Diff("mipmaps", ti.mipmapEnabled, v)); if (apply) ti.mipmapEnabled = v; }
                        break;
                    }
                    case "srgb":
                    case "srgbtexture":
                    {
                        var v = AsBool(val);
                        if (ti.sRGBTexture != v) { diffs.Add(Diff("sRGB", ti.sRGBTexture, v)); if (apply) ti.sRGBTexture = v; }
                        break;
                    }
                    case "readable":
                    case "isreadable":
                    {
                        var v = AsBool(val);
                        if (ti.isReadable != v) { diffs.Add(Diff("isReadable", ti.isReadable, v)); if (apply) ti.isReadable = v; }
                        break;
                    }
                    case "maxtexturesize":
                    {
                        var v = AsInt(val);
                        if (ti.maxTextureSize != v) { diffs.Add(Diff(key, ti.maxTextureSize, v)); if (apply) ti.maxTextureSize = v; }
                        break;
                    }
                    case "wrapmode":
                    {
                        var v = AsEnum<TextureWrapMode>(val, key);
                        if (ti.wrapMode != v) { diffs.Add(Diff(key, ti.wrapMode, v)); if (apply) ti.wrapMode = v; }
                        break;
                    }
                    case "filtermode":
                    {
                        var v = AsEnum<FilterMode>(val, key);
                        if (ti.filterMode != v) { diffs.Add(Diff(key, ti.filterMode, v)); if (apply) ti.filterMode = v; }
                        break;
                    }
                    case "npotscale":
                    {
                        var v = AsEnum<TextureImporterNPOTScale>(val, key);
                        if (ti.npotScale != v) { diffs.Add(Diff(key, ti.npotScale, v)); if (apply) ti.npotScale = v; }
                        break;
                    }
                    case "compression":
                    {
                        var v = AsEnum<TextureImporterCompression>(val, key);
                        if (ti.textureCompression != v) { diffs.Add(Diff(key, ti.textureCompression, v)); if (apply) ti.textureCompression = v; }
                        break;
                    }
                    case "alphasource":
                    {
                        var v = AsEnum<TextureImporterAlphaSource>(val, key);
                        if (ti.alphaSource != v) { diffs.Add(Diff(key, ti.alphaSource, v)); if (apply) ti.alphaSource = v; }
                        break;
                    }
                    case "texturetype":
                    {
                        var v = AsEnum<TextureImporterType>(val, key);
                        if (ti.textureType != v) { diffs.Add(Diff(key, ti.textureType, v)); if (apply) ti.textureType = v; }
                        break;
                    }
                    default:
                        log.Add("SKIPPED setting '" + key + "' on " + path
                                + " - known: alphaIsTransparency, mipmaps, sRGB, readable, maxTextureSize, "
                                + "wrapMode, filterMode, npotScale, compression, alphaSource, textureType");
                        break;
                }
            }

            return diffs;
        }

        static string Diff(string key, object from, object to)
        {
            return key + ": " + from + " -> " + to;
        }

        // ================================================================== flipbook

        public static string Flipbook(Dictionary<string, object> args)
        {
            var frames = new List<string>();

            var explicitFrames = JsonReader.GetArray(args, "frames");
            if (explicitFrames != null)
            {
                foreach (var o in explicitFrames)
                {
                    var s = o as string;
                    if (!string.IsNullOrEmpty(s)) frames.Add(s);
                }
            }
            else
            {
                string selectError;
                var found = ResolveTargets(args, out selectError);
                if (selectError != null) return VfxAiKernelApi.Error(selectError);
                frames.AddRange(found);
            }

            if (frames.Count < 2)
                return VfxAiKernelApi.Error("need at least two frames - pass 'frames' in order, "
                                            + "or 'folder' (+ optional 'filter') to take every texture in it by name");

            foreach (var f in frames)
            {
                if (!IsAssetPath(f)) return VfxAiKernelApi.Error(NotAnAssetPath(f, "frame"));

                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
                    return VfxAiKernelApi.Error("frame '" + f + "' is " + ext + " - this op reads the source file "
                                                + "directly, so frames must be PNG or JPG");
            }

            int columns = JsonReader.GetInt(args, "columns", 0);
            int rows = JsonReader.GetInt(args, "rows", 0);
            if (columns <= 0 && rows <= 0)
            {
                columns = Mathf.CeilToInt(Mathf.Sqrt(frames.Count));
                rows = Mathf.CeilToInt(frames.Count / (float)columns);
            }
            else if (columns <= 0) columns = Mathf.CeilToInt(frames.Count / (float)rows);
            else if (rows <= 0) rows = Mathf.CeilToInt(frames.Count / (float)columns);

            if (columns * rows < frames.Count)
                return VfxAiKernelApi.Error("grid " + columns + "x" + rows + " holds " + (columns * rows)
                                            + " frames but " + frames.Count + " were given");

            // Load every source first: the largest frame decides the cell size when none is given.
            var sources = new List<Texture2D>();
            var log = new List<string>();
            try
            {
                int maxDim = 0;
                foreach (var f in frames)
                {
                    var full = ToAbsolute(f);
                    if (!File.Exists(full)) { Cleanup(sources); return VfxAiKernelApi.Error("frame not found: " + f); }

                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(File.ReadAllBytes(full)))
                    {
                        UnityEngine.Object.DestroyImmediate(tex);
                        Cleanup(sources);
                        return VfxAiKernelApi.Error("could not decode " + f);
                    }
                    sources.Add(tex);
                    maxDim = Mathf.Max(maxDim, Mathf.Max(tex.width, tex.height));
                }

                int cell = JsonReader.GetInt(args, "cellSize", 0);
                if (cell <= 0) cell = Mathf.NextPowerOfTwo(maxDim);
                while (Mathf.Max(cell * columns, cell * rows) > kMaxSheetSize && cell > 8) cell /= 2;
                if (cell < 8) { Cleanup(sources); return VfxAiKernelApi.Error("grid too large: cell size collapsed below 8px"); }
                if (cell != Mathf.NextPowerOfTwo(maxDim))
                    log.Add("cell size " + cell + "px (largest frame is " + maxDim + "px)");

                var stretch = string.Equals(JsonReader.GetString(args, "fit", "contain"), "stretch",
                                            StringComparison.OrdinalIgnoreCase);

                var sheetW = cell * columns;
                var sheetH = cell * rows;
                var sheet = new Texture2D(sheetW, sheetH, TextureFormat.RGBA32, false, true);
                var clear = new Color32[sheetW * sheetH];   // transparent black, not white: additive-safe
                sheet.SetPixels32(clear);

                for (int i = 0; i < sources.Count; i++)
                {
                    var src = sources[i];
                    int cx = i % columns;
                    int cy = i / columns;
                    // Unity reads flipbooks left to right, top to bottom; SetPixels counts from the
                    // bottom, so the first row of frames goes to the LAST row of cells.
                    int originX = cx * cell;
                    int originY = (rows - 1 - cy) * cell;

                    float scale = stretch ? 1f : Mathf.Min(cell / (float)src.width, cell / (float)src.height);
                    int drawW = stretch ? cell : Mathf.Max(1, Mathf.RoundToInt(src.width * scale));
                    int drawH = stretch ? cell : Mathf.Max(1, Mathf.RoundToInt(src.height * scale));
                    int padX = (cell - drawW) / 2;
                    int padY = (cell - drawH) / 2;

                    var block = new Color[drawW * drawH];
                    for (int y = 0; y < drawH; y++)
                    {
                        float v = (y + 0.5f) / drawH;
                        for (int x = 0; x < drawW; x++)
                        {
                            float u = (x + 0.5f) / drawW;
                            block[y * drawW + x] = src.GetPixelBilinear(u, v);
                        }
                    }
                    sheet.SetPixels(originX + padX, originY + padY, drawW, drawH, block);
                }

                sheet.Apply();

                var outPath = ResolveOutputPath(args, frames[0], columns, rows);
                if (!IsAssetPath(outPath)) return VfxAiKernelApi.Error(NotAnAssetPath(outPath, "output"));

                var outAbs = ToAbsolute(outPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outAbs));
                File.WriteAllBytes(outAbs, sheet.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(sheet);

                AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);

                var ti = AssetImporter.GetAtPath(outPath) as TextureImporter;
                if (ti != null)
                {
                    ti.textureType = TextureImporterType.Default;
                    ti.alphaIsTransparency = true;
                    ti.alphaSource = TextureImporterAlphaSource.FromInput;
                    ti.wrapMode = TextureWrapMode.Clamp;
                    ti.mipmapEnabled = false;          // mips blend neighbouring frames together
                    ti.npotScale = TextureImporterNPOTScale.None;
                    ti.SaveAndReimport();
                }
                else log.Add("could not reach the importer for " + outPath + " - set it up by hand");

                var w = new JsonWriter(true);
                w.BeginObject();
                w.Prop("status", "ok");
                w.Prop("path", outPath);
                w.Prop("columns", columns);
                w.Prop("rows", rows);
                w.Prop("frames", frames.Count);
                w.Prop("cellSize", cell);
                w.Prop("width", sheetW);
                w.Prop("height", sheetH);
                if (frames.Count < columns * rows)
                    log.Add((columns * rows - frames.Count) + " empty cell(s) at the end - a Flipbook Player "
                            + "running the full grid will flash blank frames; cap Tex Index instead");
                w.BeginObject("use");
                w.Prop("uvMode", "Flipbook");
                w.Prop("flipbookLayout", "Texture2D");
                w.Key("flipBookSize").BeginArray().Value(columns).Value(rows).EndArray();
                w.EndObject();
                w.BeginArray("log");
                foreach (var l in log) w.Value(l);
                w.EndArray();
                w.EndObject();
                return w.ToString();
            }
            finally
            {
                Cleanup(sources);
            }
        }

        static void Cleanup(List<Texture2D> textures)
        {
            foreach (var t in textures)
                if (t != null) UnityEngine.Object.DestroyImmediate(t);
            textures.Clear();
        }

        /// <summary>Output path, with the grid appended to the name so a later scan can read it back.</summary>
        static string ResolveOutputPath(Dictionary<string, object> args, string firstFrame, int columns, int rows)
        {
            var outPath = JsonReader.GetString(args, "output", null);
            if (string.IsNullOrEmpty(outPath))
            {
                var stem = Path.GetFileNameWithoutExtension(firstFrame).TrimEnd('0', '1', '2', '3', '4',
                                                                                '5', '6', '7', '8', '9');
                if (stem.Length == 0) stem = "flipbook";
                outPath = Path.GetDirectoryName(firstFrame).Replace('\\', '/') + "/" + stem + ".png";
            }

            outPath = outPath.Replace('\\', '/');
            if (!outPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) outPath += ".png";

            var name = Path.GetFileNameWithoutExtension(outPath);
            var grid = "_" + columns + "x" + rows;
            if (!name.EndsWith(grid, StringComparison.Ordinal))
                outPath = Path.GetDirectoryName(outPath).Replace('\\', '/') + "/" + name + grid + ".png";

            return outPath;
        }

        // ================================================================== shared helpers

        /// <summary>
        /// Everything here addresses imported assets, so every path must be project-relative and
        /// start with Assets/. Refusing anything else on the spot is what stops the classic mistake:
        /// pointing at the skill's own texture bundle, which lives outside the project, has no GUID,
        /// and cannot be referenced by a graph. Copy it into Assets/ first.
        /// </summary>
        static string NotAnAssetPath(string path, string what)
        {
            return what + " '" + path + "' is not inside Assets/. Only imported assets have a GUID and "
                   + "can be referenced; a source folder outside the project (the skill's own "
                   + "assets/Textures bundle, for one) has to be copied into Assets/ and imported first.";
        }

        static bool IsAssetPath(string path)
        {
            return !string.IsNullOrEmpty(path)
                   && path.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }

        static string[] ResolveFolders(Dictionary<string, object> args, out string error)
        {
            error = null;
            var folders = new List<string>();

            var one = JsonReader.GetString(args, "folder", null);
            if (!string.IsNullOrEmpty(one)) folders.Add(one.TrimEnd('/'));

            var many = JsonReader.GetArray(args, "folders");
            if (many != null)
                foreach (var o in many)
                {
                    var s = o as string;
                    if (!string.IsNullOrEmpty(s)) folders.Add(s.TrimEnd('/'));
                }

            foreach (var f in folders)
            {
                if (!IsAssetPath(f)) { error = NotAnAssetPath(f, "folder"); return null; }
                if (!AssetDatabase.IsValidFolder(f))
                {
                    error = "folder '" + f + "' does not exist in the project";
                    return null;
                }
            }

            if (folders.Count == 0) folders.Add("Assets");
            return folders.ToArray();
        }

        /// <summary>Explicit 'paths', otherwise every texture under 'folder' matching 'filter', by name.</summary>
        static List<string> ResolveTargets(Dictionary<string, object> args, out string error)
        {
            error = null;
            var result = new List<string>();

            var explicitPaths = JsonReader.GetArray(args, "paths");
            if (explicitPaths != null)
            {
                foreach (var o in explicitPaths)
                {
                    var s = o as string;
                    if (string.IsNullOrEmpty(s)) continue;
                    if (!IsAssetPath(s)) { error = NotAnAssetPath(s, "path"); return null; }
                    result.Add(s);
                }
                if (result.Count > 0) return result;
            }

            if (JsonReader.GetString(args, "folder", null) == null && JsonReader.GetArray(args, "folders") == null)
                return result;

            var folders = ResolveFolders(args, out error);
            if (error != null) return null;

            var filter = (JsonReader.GetString(args, "filter", null) ?? "").ToLowerInvariant();
            foreach (var g in AssetDatabase.FindAssets("t:Texture2D", folders))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(p)) continue;
                if (filter.Length > 0 && p.ToLowerInvariant().IndexOf(filter, StringComparison.Ordinal) < 0) continue;
                result.Add(p);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        static string ToAbsolute(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }

        static bool AsBool(object v)
        {
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0.0;
            bool b;
            if (v != null && bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out b)) return b;
            throw new FormatException("expected true/false, got '" + v + "'");
        }

        static int AsInt(object v)
        {
            if (v is double) return (int)Math.Round((double)v);
            int i;
            if (v != null && int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                                          NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
            throw new FormatException("expected a number, got '" + v + "'");
        }

        static T AsEnum<T>(object v, string key) where T : struct
        {
            var s = Convert.ToString(v, CultureInfo.InvariantCulture);
            T parsed;
            if (!string.IsNullOrEmpty(s) && Enum.TryParse(s, true, out parsed)) return parsed;
            throw new FormatException("bad value '" + s + "' for " + key + " - options: "
                                      + string.Join(", ", Enum.GetNames(typeof(T))));
        }
    }
}
