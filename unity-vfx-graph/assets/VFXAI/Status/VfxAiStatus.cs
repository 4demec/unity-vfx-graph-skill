// VfxAiStatus.cs
// Deliberately isolated watchdog assembly: it references NOTHING (see VFXAI.Status.asmdef), so it
// still compiles and still reports when the kernel or bridge assemblies are broken. Without this,
// a compile error in the VFX-dependent code would also take out its own error reporter.
//
// Writes to <ProjectRoot>/VFXAI_Reports/:
//   heartbeat.txt      touched every few seconds while the editor is running
//   editor_status.json environment + which VFX AI assemblies actually loaded
//   compile_log.json   errors and warnings from the last script compilation

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace VfxAi.Status
{
    [InitializeOnLoad]
    public static class VfxAiStatus
    {
        public const string kVersion = "0.1.0";

        const double kHeartbeatSeconds = 5.0;
        static double s_NextHeartbeat;
        static readonly List<CompileEntry> s_Entries = new List<CompileEntry>();

        struct CompileEntry
        {
            public string assembly;
            public string type;
            public string message;
            public string file;
            public int line;
            public int column;
        }

        static string Dir
        {
            get
            {
                var d = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "VFXAI_Reports"));
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static VfxAiStatus()
        {
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            EditorApplication.update -= OnUpdate;
            EditorApplication.update += OnUpdate;

            EditorApplication.delayCall += () => TryWriteStatus("domain-reload");
        }

        [MenuItem("Tools/VFX AI/Write Editor Status")]
        static void WriteStatusMenu()
        {
            TryWriteStatus("manual");
            Debug.Log("[VFX AI] editor status written to " + Dir);
        }

        static void OnUpdate()
        {
            if (EditorApplication.timeSinceStartup < s_NextHeartbeat) return;
            s_NextHeartbeat = EditorApplication.timeSinceStartup + kHeartbeatSeconds;
            try
            {
                File.WriteAllText(Path.Combine(Dir, "heartbeat.txt"),
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\n" +
                    (EditorApplication.isCompiling ? "compiling" : "idle") + "\n" +
                    (EditorApplication.isPlaying ? "playing" : "edit-mode") + "\n");
            }
            catch { /* disk hiccup: never spam the console from update */ }
        }

        static void OnCompilationStarted(object ctx)
        {
            s_Entries.Clear();
        }

        static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null) return;
            var asmName = Path.GetFileNameWithoutExtension(assemblyPath);
            foreach (var m in messages)
            {
                s_Entries.Add(new CompileEntry
                {
                    assembly = asmName,
                    type = m.type.ToString(),
                    message = m.message,
                    file = m.file,
                    line = m.line,
                    column = m.column,
                });
            }
        }

        static void OnCompilationFinished(object ctx)
        {
            try { WriteCompileLog(); } catch { }
            try { TryWriteStatus("compilation-finished"); } catch { }
        }

        static void WriteCompileLog()
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"timestampUtc\": ").Append(Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
            int errors = 0, warnings = 0;
            foreach (var e in s_Entries)
            {
                if (e.type == "Error") errors++;
                else if (e.type == "Warning") warnings++;
            }
            sb.Append(",\n  \"errorCount\": ").Append(errors);
            sb.Append(",\n  \"warningCount\": ").Append(warnings);
            sb.Append(",\n  \"messages\": [");
            bool first = true;
            foreach (var e in s_Entries)
            {
                if (e.type == "Warning" && errors > 0) continue; // keep the log readable when it matters
                if (!first) sb.Append(',');
                first = false;
                sb.Append("\n    {")
                  .Append("\"assembly\": ").Append(Q(e.assembly))
                  .Append(", \"type\": ").Append(Q(e.type))
                  .Append(", \"file\": ").Append(Q(e.file))
                  .Append(", \"line\": ").Append(e.line)
                  .Append(", \"column\": ").Append(e.column)
                  .Append(", \"message\": ").Append(Q(e.message))
                  .Append('}');
            }
            sb.Append("\n  ]\n}\n");
            File.WriteAllText(Path.Combine(Dir, "compile_log.json"), sb.ToString());
        }

        static void TryWriteStatus(string reason)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"statusVersion\": ").Append(Q(kVersion)).Append(",\n");
            sb.Append("  \"reason\": ").Append(Q(reason)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"unityVersion\": ").Append(Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"projectPath\": ").Append(Q(Path.GetFullPath(Path.Combine(Application.dataPath, "..")))).Append(",\n");
            sb.Append("  \"isCompiling\": ").Append(EditorApplication.isCompiling ? "true" : "false").Append(",\n");
            sb.Append("  \"loadedComponents\": {\n");
            sb.Append("    \"probe\": ").Append(HasType("VfxAi.Probe.VfxAiProbe") ? "true" : "false").Append(",\n");
            sb.Append("    \"kernel\": ").Append(HasType("VfxAi.Kernel.VfxAiCatalog") ? "true" : "false").Append(",\n");
            sb.Append("    \"builder\": ").Append(HasType("VfxAi.Kernel.VfxAiBuilder") ? "true" : "false").Append(",\n");
            sb.Append("    \"bridge\": ").Append(HasType("VfxAi.Bridge.VfxAiJobRunner") ? "true" : "false").Append("\n");
            sb.Append("  }\n}\n");
            File.WriteAllText(Path.Combine(Dir, "editor_status.json"), sb.ToString());
        }

        static bool HasType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (asm.GetType(fullName, false) != null) return true; }
                catch { }
            }
            return false;
        }

        static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
