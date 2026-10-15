using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Resonator.Pipeline.Targets;

/// <summary>
/// Reads target.json files. A spec may <c>"extends": "&lt;Set&gt;/&lt;Target&gt;"</c> (resolved against the
/// directory holding the sets); the chain is merged root ancestor first:
///   scalars              a layer overrides its parent ("name" only on the leaf)
///   select_types         a layer replaces its parent's
///   exclude_types, exclude_refs, refs, fix_ctor_types, keep_dirs, defines
///                        accumulate down the chain
///   packages, properties merge by key
///   package / source     the last one wins (a target has one kind)
///   patches/             every layer's dir, applied parent first
/// </summary>
public static class TargetSpecLoader
{
    const string FrooxEngineWeaver = "FrooxEngine.Weaver.dll";
    static readonly string[] AccumulatedLists = ["exclude_types", "exclude_refs", "refs", "fix_ctor_types", "keep_dirs", "defines"];

    /// <summary>Load and merge the spec chain of <paramref name="targetDir"/>; the name defaults to the dir name.</summary>
    public static TargetSpec Load(string targetDir)
    {
        targetDir = Path.GetFullPath(targetDir);
        string name = Path.GetFileName(targetDir);
        string? assembly = null, outAssembly = null, weave = null, tfm = null;
        string? weaverCheck = FrooxEngineWeaver;
        string[] selectTypes = [];
        PackageSpec? package = null;
        SourceSpec? source = null;
        CalliSpec? calli = null;
        var lists = AccumulatedLists.ToDictionary(k => k, _ => new List<string>());
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var patchDirs = new List<string>();

        foreach (var (dir, spec) in Chain(targetDir))
        {
            if (dir.Equals(targetDir, StringComparison.OrdinalIgnoreCase)) name = String(spec, "name") ?? name;
            assembly = String(spec, "assembly") ?? assembly;
            outAssembly = String(spec, "out_assembly") ?? outAssembly;
            weave = String(spec, "weave") ?? weave;
            tfm = String(spec, "tfm") ?? tfm;
            if (spec.TryGetPropertyValue("weaver_check", out var check)) weaverCheck = check?.GetValue<string>();   // null disables it
            if (Has(spec, "select_types")) selectTypes = Strings(spec, "select_types");
            foreach (var (key, list) in lists) list.AddRange(Strings(spec, key));

            if (spec["package"] is JsonObject p)
            {
                package = new PackageSpec(p["id"]!.GetValue<string>(), p["version"]!.GetValue<string>());
                source = null;
            }
            if (spec["source"] is JsonObject s)
            {
                source = new SourceSpec(String(s, "git"), String(s, "archive"), String(s, "ref") ?? "", String(s, "dir") ?? "",
                    s["exclude"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? []);
                package = null;
            }
            if (spec["calli"] is JsonObject c)
                calli = new CalliSpec(c["attribute"]!.GetValue<string>(), String(c, "remove_type_attribute"));
            Merge(spec, "packages", packages);
            Merge(spec, "properties", properties);
            patchDirs.Add(Path.Combine(dir, "patches"));
        }

        string[] Accumulated(string key) => lists[key].Distinct(StringComparer.Ordinal).ToArray();
        var keepDirs = Accumulated("keep_dirs");

        if (string.IsNullOrEmpty(assembly)) throw new ArgumentException($"spec for '{name}' has no assembly");
        if (source != null && (source.Git == null) == (source.Archive == null))
            throw new ArgumentException($"spec for '{name}': source needs exactly one of \"git\" / \"archive\"");
        if (source?.Git != null && source.Ref == "")
            throw new ArgumentException($"spec for '{name}': a git source needs a pinned \"ref\" (tag or commit)");

        var result = new TargetSpec
        {
            Name = name,
            Dir = targetDir,
            Assembly = assembly,
            OutAssembly = outAssembly,
            Tfm = tfm ?? "net10.0",
            Weave = ParseWeave(weave, name),
            // the engine's own weaver is always checked; elsewhere "weaver_check": null switches it off
            WeaverCheck = weaverCheck ?? (assembly.Equals("FrooxEngine.dll", StringComparison.OrdinalIgnoreCase) ? FrooxEngineWeaver : null),
            Package = package,
            Source = source,
            Calli = calli,
            SelectTypes = selectTypes,
            ExcludeTypes = Accumulated("exclude_types"),
            ExcludeRefs = Accumulated("exclude_refs"),
            Refs = Accumulated("refs"),
            FixCtorTypes = Accumulated("fix_ctor_types"),
            KeepDirs = keepDirs.Length > 0 ? keepDirs : ["Injected"],
            Defines = Accumulated("defines"),
            Packages = packages,
            Properties = properties,
            PatchDirs = patchDirs,
        };
        // a package / source target has nothing weaved to prepare
        return result.Kind == TargetKind.Decompile ? result : result with { Weave = WeaveMode.None };
    }

    /// <summary>One raw value of the merged chain (no validation) - for selecting targets by assembly.</summary>
    public static string? RawValue(string targetDir, string key) =>
        Chain(Path.GetFullPath(targetDir)).Select(c => String(c.Spec, key)).LastOrDefault(v => v != null);

    /// <summary>The target dirs <paramref name="targetDir"/> extends, directly or not.</summary>
    public static IEnumerable<string> Ancestors(string targetDir) =>
        Chain(Path.GetFullPath(targetDir)).SkipLast(1).Select(c => c.Dir);

    /// <summary>The spec chain of a target dir, root ancestor first.</summary>
    static List<(string Dir, JsonObject Spec)> Chain(string targetDir)
    {
        var chain = new List<(string Dir, JsonObject Spec)>();
        for (string? dir = targetDir; dir != null;)
        {
            string file = Path.Combine(dir, "target.json");
            if (!File.Exists(file)) throw new ArgumentException($"no spec: {file}");
            if (chain.Any(c => c.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"extends cycle at {file}");
            var spec = JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? throw new ArgumentException($"bad spec: {file}");
            chain.Insert(0, (dir, spec));
            dir = Extends(spec, setsRoot: Path.GetDirectoryName(Path.GetDirectoryName(dir)!)!);
        }
        return chain;
    }

    /// <summary>The set chain, root ancestor first: a set's set.json may <c>"extends": "&lt;Set&gt;"</c>.</summary>
    public static List<string> SetChain(string setDir)
    {
        var chain = new List<string>();
        for (string? dir = Path.GetFullPath(setDir); dir != null;)
        {
            if (chain.Contains(dir, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"set extends cycle at {dir}");
            if (!Directory.Exists(dir)) throw new ArgumentException($"target set not found: {dir}");
            chain.Insert(0, dir);
            string file = Path.Combine(dir, "set.json");
            var spec = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))?.AsObject() : null;
            dir = spec == null ? null : Extends(spec, setsRoot: Path.GetDirectoryName(dir)!);
        }
        return chain;
    }

    static string? Extends(JsonObject spec, string setsRoot) =>
        String(spec, "extends") is { } extends ? Path.GetFullPath(Path.Combine(setsRoot, extends)) : null;

    static WeaveMode ParseWeave(string? weave, string target) => weave switch
    {
        null or "deweave" => WeaveMode.Deweave,
        "flatten" => WeaveMode.Flatten,
        "none" => WeaveMode.None,
        _ => throw new ArgumentException($"spec for '{target}': unknown weave '{weave}' (expected deweave|flatten|none)"),
    };

    // ---------------------------------------------------------------- JSON access

    static bool Has(JsonObject o, string key) => o.TryGetPropertyValue(key, out var v) && v != null;

    static string? String(JsonObject o, string key) => Has(o, key) ? o[key]!.GetValue<string>() : null;

    static string[] Strings(JsonObject o, string key) => Has(o, key)
        ? o[key]!.AsArray().Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray()
        : [];

    static void Merge(JsonObject o, string key, Dictionary<string, string> into)
    {
        if (o[key] is JsonObject map)
            foreach (var (k, v) in map) into[k] = v!.GetValue<string>();
    }
}
