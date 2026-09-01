// VfxAiJobRunner.cs
// File-driven job queue: the AI drops <name>.<op>.job files into <ProjectRoot>/VFXAI_Jobs/ and
// reads answers from <ProjectRoot>/VFXAI_Results/. Runs off EditorApplication.update, which keeps
// ticking while Unity is unfocused, so the loop does not depend on the user clicking anything.
//
// This assembly references NOTHING (see VFXAI.Bridge.asmdef) and reaches the kernel by reflection.
// That isolation is deliberate: if the kernel stops compiling, this still runs and can still
// process a "refresh" job to pick up the fix.
//
// Job file naming:  anything.<op>.job     e.g. 001.catalog.job, fireball.apply.job
// File contents:    JSON arguments for the op (may be empty)
//
// Ops that modify project assets are held for approval in the VFX AI control panel.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace VfxAi.Bridge
{
    [InitializeOnLoad]
    public static class VfxAiJobRunner
    {
        public const string kVersion = "0.2.0";
        const double kPollSeconds = 1.0;

        static double s_NextPoll;
        static MethodInfo s_KernelInvoke;
        static bool s_KernelSearched;

        public class PendingJob
        {
            public string path;
            public string fileName;
            public string op;
            public string args;
            public DateTime seenUtc;

            /// <summary>Queue order. Jobs against one asset must be approved in this order.</summary>
            public int seq;

            /// <summary>The asset this job targets, so same-file jobs can be serialised.</summary>
            public string target;
        }

        static int s_NextSeq;

        public static readonly List<PendingJob> pending = new List<PendingJob>();
        static readonly HashSet<string> s_Queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static readonly HashSet<string> kMutatingOps =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "apply", "write", "delete", "duplicate", "scene", "texconfig", "flipbook" };

        // ------------------------------------------------------------------ paths

        public static string projectRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
        }

        public static string jobsDir { get { return Ensure(Path.Combine(projectRoot, "VFXAI_Jobs")); } }
        public static string processedDir { get { return Ensure(Path.Combine(jobsDir, "processed")); } }
        public static string resultsDir { get { return Ensure(Path.Combine(projectRoot, "VFXAI_Results")); } }

        static string Ensure(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        // ------------------------------------------------------------------ prefs

        public static bool bridgeEnabled
        {
            get { return EditorPrefs.GetBool("VfxAi.BridgeEnabled", true); }
            set { EditorPrefs.SetBool("VfxAi.BridgeEnabled", value); }
        }

        public static bool autoApprove
        {
            get { return EditorPrefs.GetBool("VfxAi.AutoApprove", false); }
            set { EditorPrefs.SetBool("VfxAi.AutoApprove", value); }
        }

        // ------------------------------------------------------------------ lifecycle

        static VfxAiJobRunner()
        {
            EditorApplication.update -= OnUpdate;
            EditorApplication.update += OnUpdate;
        }

        static void OnUpdate()
        {
            if (!bridgeEnabled) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup < s_NextPoll) return;
            s_NextPoll = EditorApplication.timeSinceStartup + kPollSeconds;

            try { Poll(); }
            catch (Exception e) { Debug.LogError("[VFX AI] bridge poll failed: " + e); }
        }

        static void Poll()
        {
            string[] files;
            try { files = Directory.GetFiles(jobsDir, "*.job", SearchOption.TopDirectoryOnly); }
            catch { return; }
            if (files.Length == 0) return;

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (var path in files)
            {
                var op = OpFromFileName(path);
                string args;
                try { args = File.ReadAllText(path); }
                catch { continue; } // still being written; try again next tick

                if (kMutatingOps.Contains(op) && !autoApprove)
                {
                    Queue(path, op, args);
                    continue;
                }

                Execute(path, op, args);
            }
        }

        static void Queue(string path, string op, string args)
        {
            if (s_Queued.Contains(path)) return;
            s_Queued.Add(path);
            pending.Add(new PendingJob
            {
                path = path,
                fileName = Path.GetFileName(path),
                op = op,
                args = args,
                seenUtc = DateTime.UtcNow,
                seq = s_NextSeq++,
                target = ExtractTarget(args),
            });
            WriteResult(path, op, DateTime.UtcNow,
                "{\"status\":\"pending\",\"message\":\"waiting for approval in the VFX AI control panel\"}");
            Debug.Log("[VFX AI] job awaiting approval: " + Path.GetFileName(path));
            VfxAiControlPanel.RepaintAll();
        }

        public static void Approve(PendingJob job)
        {
            Dequeue(job);
            Execute(job.path, job.op, job.args);
        }

        public static void Reject(PendingJob job, string reason)
        {
            Dequeue(job);
            WriteResult(job.path, job.op, DateTime.UtcNow,
                "{\"status\":\"rejected\",\"message\":" + Q(reason ?? "rejected by user") + "}");
            MoveToProcessed(job.path);
        }

        /// <summary>
        /// True when an older pending job targets the same asset. Edits address nodes by ids taken
        /// from an inspect of a particular graph state, so applying a later job first can silently
        /// hit the wrong nodes. Serialising per asset makes that impossible rather than unlikely.
        /// </summary>
        public static PendingJob BlockedBy(PendingJob job)
        {
            if (job == null || string.IsNullOrEmpty(job.target)) return null;
            PendingJob oldest = null;
            foreach (var other in pending)
            {
                if (ReferenceEquals(other, job)) continue;
                if (other.seq >= job.seq) continue;
                if (!string.Equals(other.target, job.target, StringComparison.OrdinalIgnoreCase)) continue;
                if (oldest == null || other.seq < oldest.seq) oldest = other;
            }
            return oldest;
        }

        /// <summary>Pull the target asset out of the payload without a JSON parser.</summary>
        static string ExtractTarget(string args)
        {
            if (string.IsNullOrEmpty(args)) return null;
            foreach (var key in new[] { "\"path\"", "\"asset\"" })
            {
                var k = args.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (k < 0) continue;
                var colon = args.IndexOf(':', k + key.Length);
                if (colon < 0) continue;
                var q1 = args.IndexOf('"', colon + 1);
                if (q1 < 0) continue;
                var q2 = args.IndexOf('"', q1 + 1);
                if (q2 < 0) continue;
                return args.Substring(q1 + 1, q2 - q1 - 1);
            }
            return null;
        }

        static void Dequeue(PendingJob job)
        {
            pending.Remove(job);
            s_Queued.Remove(job.path);
        }

        // ------------------------------------------------------------------ execution

        static void Execute(string jobPath, string op, string args)
        {
            var started = DateTime.UtcNow;
            string payload;
            bool refreshAfter = false;

            try
            {
                switch (op)
                {
                    case "ping":
                        payload = "{\"status\":\"ok\",\"bridgeVersion\":" + Q(kVersion)
                                  + ",\"unityVersion\":" + Q(Application.unityVersion)
                                  + ",\"kernelLoaded\":" + (ResolveKernel() != null ? "true" : "false") + "}";
                        break;

                    case "refresh":
                        payload = "{\"status\":\"ok\",\"message\":\"refresh + script compilation requested\"}";
                        refreshAfter = true;
                        break;

                    default:
                        payload = InvokeKernel(op, args);
                        break;
                }
            }
            catch (Exception e)
            {
                payload = "{\"status\":\"error\",\"message\":" + Q(e.GetType().Name + ": " + e.Message)
                          + ",\"stack\":" + Q(e.StackTrace ?? "") + "}";
            }

            WriteResult(jobPath, op, started, payload);
            MoveToProcessed(jobPath);

            // Refresh last, and inline rather than via delayCall: it can trigger a domain reload that
            // would kill this call mid-flight, so the result must already be written and archived.
            //
            // A bare AssetDatabase.Refresh() is deferred while the editor sits in the background, so
            // new script files are never even scanned. Forcing a synchronous recursive import of the
            // tooling folder first is what actually gets them picked up without a focus click.
            if (refreshAfter)
            {
                try
                {
                    const string folder = "Assets/VFXAI";
                    if (AssetDatabase.IsValidFolder(folder))
                    {
                        AssetDatabase.ImportAsset(folder,
                            ImportAssetOptions.ImportRecursive
                            | ImportAssetOptions.ForceUpdate
                            | ImportAssetOptions.ForceSynchronousImport);
                    }
                    // Plain Refresh here on purpose. A global ForceSynchronousImport flushes any
                    // asset Unity has dirtied in memory - including ones inside immutable packages,
                    // which then get rewritten in the package cache and trigger the "assets located
                    // in immutable packages were unexpectedly altered" warning. The scoped import
                    // above is what actually gets our own scripts noticed.
                    AssetDatabase.Refresh();
                    CompilationPipeline.RequestScriptCompilation();
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[VFX AI] refresh failed: " + e.Message);
                }
            }
        }

        static string InvokeKernel(string op, string args)
        {
            var m = ResolveKernel();
            if (m == null)
            {
                return "{\"status\":\"error\",\"message\":\"kernel not loaded "
                       + "(VfxAi.Kernel.VfxAiKernelApi.Invoke not found - check compile_log.json)\"}";
            }

            var result = m.Invoke(null, new object[] { op, args }) as string;
            return string.IsNullOrEmpty(result)
                ? "{\"status\":\"error\",\"message\":\"kernel returned nothing\"}"
                : result;
        }

        static MethodInfo ResolveKernel()
        {
            if (s_KernelSearched && s_KernelInvoke != null) return s_KernelInvoke;
            s_KernelSearched = true;
            s_KernelInvoke = null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType("VfxAi.Kernel.VfxAiKernelApi", false); }
                catch { }
                if (t == null) continue;

                var m = t.GetMethod("Invoke",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(string) },
                    null);

                if (m != null) { s_KernelInvoke = m; break; }
            }
            return s_KernelInvoke;
        }

        // ------------------------------------------------------------------ io

        static string OpFromFileName(string path)
        {
            // "<anything>.<op>.job" -> op ; a bare "<op>.job" also works
            var name = Path.GetFileNameWithoutExtension(path); // strips .job
            var dot = name.LastIndexOf('.');
            var op = dot >= 0 ? name.Substring(dot + 1) : name;
            return op.Trim().ToLowerInvariant();
        }

        static void WriteResult(string jobPath, string op, DateTime startedUtc, string payloadJson)
        {
            try
            {
                var baseName = Path.GetFileNameWithoutExtension(jobPath);
                var sb = new StringBuilder();
                sb.Append("{\n  \"job\": ").Append(Q(Path.GetFileName(jobPath)));
                sb.Append(",\n  \"op\": ").Append(Q(op));
                sb.Append(",\n  \"startedUtc\": ").Append(Q(startedUtc.ToString("o", CultureInfo.InvariantCulture)));
                sb.Append(",\n  \"finishedUtc\": ").Append(Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
                sb.Append(",\n  \"result\": ").Append(string.IsNullOrEmpty(payloadJson) ? "null" : payloadJson);
                sb.Append("\n}\n");
                File.WriteAllText(Path.Combine(resultsDir, baseName + ".result.json"), sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogError("[VFX AI] could not write job result: " + e.Message);
            }
        }

        static void MoveToProcessed(string jobPath)
        {
            try
            {
                if (!File.Exists(jobPath)) return;
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var dest = Path.Combine(processedDir, stamp + "-" + Path.GetFileName(jobPath));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(jobPath, dest);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VFX AI] could not archive job file: " + e.Message);
                try { File.Delete(jobPath); } catch { }
            }
        }

        internal static string Q(string s)
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
