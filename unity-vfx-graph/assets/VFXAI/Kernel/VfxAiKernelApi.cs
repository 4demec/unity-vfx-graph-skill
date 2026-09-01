// VfxAiKernelApi.cs
// The single public entry point into the VFX-internal kernel: string op + string JSON args in,
// string JSON out. Everything else in the kernel stays private to Unity.VisualEffectGraph.Editor.
//
// The bridge assembly calls this by REFLECTION on purpose - it must keep working even when this
// assembly fails to compile, so that a "refresh" job can still pick up a fix.

using System;

namespace VfxAi.Kernel
{
    public static class VfxAiKernelApi
    {
        public const string kApiVersion = "0.2.0";

        public static string Invoke(string op, string argsJson)
        {
            try
            {
                System.Collections.Generic.Dictionary<string, object> args = null;
                if (!string.IsNullOrWhiteSpace(argsJson))
                {
                    try { args = JsonReader.AsObject(JsonReader.Parse(argsJson)); }
                    catch (Exception e) { return Error("job payload is not valid JSON: " + e.Message); }
                }
                if (args == null) args = new System.Collections.Generic.Dictionary<string, object>();

                switch (op)
                {
                    case "list":
                        return VfxAiBuilder.List(args);

                    case "inspect":
                        return VfxAiBuilder.Inspect(args);

                    case "apply":
                        return VfxAiBuilder.Apply(args);

                    case "scene":
                        return VfxAiBuilder.Scene(args);

                    case "textures":
                        return VfxAiTextures.Scan(args);

                    case "texconfig":
                        return VfxAiTextures.Configure(args);

                    case "flipbook":
                        return VfxAiTextures.Flipbook(args);

                    case "version":
                    {
                        var w = Begin();
                        w.Prop("kernelVersion", VfxAiCatalog.kKernelVersion);
                        w.Prop("apiVersion", kApiVersion);
                        return End(w);
                    }

                    case "catalog":
                    {
                        var dir = VfxAiCatalog.Dump();
                        var w = Begin();
                        w.Prop("outputDirectory", dir);
                        return End(w);
                    }

                    default:
                        return Error("unknown op '" + op + "'");
                }
            }
            catch (Exception e)
            {
                return Error(e.GetType().Name + ": " + e.Message, e.StackTrace);
            }
        }

        static JsonWriter Begin()
        {
            var w = new JsonWriter(true);
            w.BeginObject();
            w.Prop("status", "ok");
            return w;
        }

        static string End(JsonWriter w)
        {
            w.EndObject();
            return w.ToString();
        }

        public static string Error(string message, string stack = null)
        {
            var w = new JsonWriter(true);
            w.BeginObject();
            w.Prop("status", "error");
            w.Prop("message", message);
            if (stack != null) w.Prop("stack", stack);
            w.EndObject();
            return w.ToString();
        }
    }
}
