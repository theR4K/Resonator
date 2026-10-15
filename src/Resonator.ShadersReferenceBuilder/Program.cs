// Resonator.ShadersReferenceBuilder - the official Resonite shaders as a reference source tree,
// plus the metadata table a renderer needs (cloud assets in, so separate from ProjectBuilder).
//
//   list    the shader versions the shipped engine lists (FrooxEngine.dll OfficialAssets.Shaders)
//   fetch   download them with the game's AssetGatherer into the bundle cache
//   unpack  bundles -> <tree>/<version>/<Shader>/ (metadata.json + Sources/**)
//   meta    <tree> -> the generated table (<out>)
//   all     fetch + unpack + meta

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Resonator.ShadersReferenceBuilder;

internal static class Program
{
    const string Usage = """
        Resonator.ShadersReferenceBuilder <list|fetch|unpack|meta|all> [options]

          list     print the shader versions listed in the shipped engine
          fetch    download them (game's AssetGatherer, anonymous, sha256-verified, cached)
          unpack   cached bundles -> <tree>/<version>/<Shader>/ (metadata.json + Sources/**)
          meta     <tree> -> generated metadata table (a partial class, AddGeneratedMeta)
          all      fetch + unpack + meta

          --resonite <dir>    Resonite install (default: %RESONITE_DIR%, else the Steam path if it exists)
          --cache <dir>       bundle cache (default: %RESONITE_SHADERS%, else <temp>/resonator/shaders)
                              full set is ~6 GB (compiled variants) - keep it somewhere permanent
          --tree <dir>        the unpacked reference tree                    (unpack, meta, all)
          --out <file>        generated table, e.g. ShaderMeta.g.cs          (meta, all)
          --namespace <ns>    namespace of the partial class                 (meta, all)
          --class <name>      the partial class (default: ShaderVariants)
          --parallel <n>      concurrent downloads (default: 8)

        version "_" = current, otherwise dated ("_30_07_2023", ...).
        """;

    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }
        string command = args[0];
        if (command is not ("list" or "fetch" or "unpack" or "meta" or "all"))
        {
            Console.Error.WriteLine($"unknown command '{command}'\n\n{Usage}");
            return 2;
        }

        string? resonite = Env("RESONITE_DIR");
        string cache = Env("RESONITE_SHADERS") ?? Path.Combine(Path.GetTempPath(), "resonator", "shaders");
        string? tree = null, outFile = null, ns = null;
        string cls = "ShaderVariants";
        int parallel = 8;
        for (int i = 1; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(args[i] + " needs a value");
            switch (args[i])
            {
                case "--resonite": resonite = Value(); break;
                case "--cache": cache = Value(); break;
                case "--tree": tree = Value(); break;
                case "--out": outFile = Value(); break;
                case "--namespace": ns = Value(); break;
                case "--class": cls = Value(); break;
                case "--parallel": parallel = int.Parse(Value()); break;
                default: Console.Error.WriteLine($"unknown option '{args[i]}'\n\n{Usage}"); return 2;
            }
        }

        bool fetch = command is "fetch" or "all", unpack = command is "unpack" or "all", meta = command is "meta" or "all";
        string? missing = (unpack || meta) && tree == null ? "--tree"
            : meta && outFile == null ? "--out"
            : meta && ns == null ? "--namespace"
            : null;
        if (missing != null)
        {
            Console.Error.WriteLine($"'{command}' needs {missing}\n\n{Usage}");
            return 2;
        }
        const string steamResonite = @"C:\Program Files (x86)\Steam\steamapps\common\Resonite";
        if (resonite == null && (command is "list" || fetch))
        {
            if (!Directory.Exists(steamResonite))
            {
                Console.Error.WriteLine($"Resonite install not found at the default {steamResonite}; set RESONITE_DIR or pass --resonite <dir>");
                return 2;
            }
            resonite = steamResonite;
        }

        try
        {
            var listed = new Lazy<List<OfficialShader>>(() => ListShaders(resonite!));
            if (command == "list")
            {
                foreach (var s in listed.Value) Console.WriteLine($"{s.Version}/{s.Shader}  {s.Url}");
                return 0;
            }
            cache = Path.GetFullPath(cache);
            if (fetch)
            {
                Console.WriteLine("=== fetch -> " + cache);
                var result = ShaderDownloader.Run(resonite!, listed.Value, cache, parallel, Console.Out);
                Console.WriteLine($"fetched {result.Downloaded}, already present {result.Present}, failed {result.Failed.Count}");
                if (result.Failed.Count > 0)
                {
                    foreach (string f in result.Failed) Console.WriteLine("  FAILED " + f);
                    return 1;
                }
            }
            if (unpack)
            {
                Console.WriteLine("=== unpack -> " + Path.GetFullPath(tree!));
                int notInTree = ShaderTree.Unpack(cache, listed.Value, Path.GetFullPath(tree!), Console.Out);
                if (notInTree > 0) Console.WriteLine($"WARNING: {notInTree} listed shader(s) are not in the tree (run fetch)");
            }
            if (meta)
            {
                string output = Path.GetFullPath(outFile!);
                Console.WriteLine("=== meta -> " + output);
                int entries = ShaderTree.WriteMeta(Path.GetFullPath(tree!), ns!, cls, output);
                Console.WriteLine($"wrote {entries} entries to {output}");
            }
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("FAIL: " + e.Message);
            return 1;
        }
    }

    static List<OfficialShader> ListShaders(string resonite)
    {
        string engine = Path.Combine(resonite, "FrooxEngine.dll");
        var listed = OfficialShaders.Read(engine);
        Console.WriteLine($"{listed.Count} shader version(s) listed in {engine} ("
            + string.Join(", ", listed.GroupBy(s => s.Version).Select(g => $"{g.Key}: {g.Count()}")) + ")");
        return listed;
    }

    static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
