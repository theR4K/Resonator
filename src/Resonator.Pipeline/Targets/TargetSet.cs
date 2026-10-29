using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Resonator.Pipeline.Targets;

/// <summary>
/// A target set: <c>targets/&lt;Set&gt;/&lt;Target&gt;/target.json + patches/*.patch</c>, data only.
/// A set's set.json may <c>"extends": "&lt;Set&gt;"</c>: it then sees every target of the set below.
/// Its own target replaces an inherited one with the same name, or one it extends
/// (ArmAginator/RenderiteShared replaces Resonator/Renderite.Shared).
/// </summary>
public sealed class TargetSet
{
    /// <summary>The set's own directory.</summary>
    public string Dir { get; }
    /// <summary>Every target the set sees (own + inherited): name -> target dir.</summary>
    public IReadOnlyDictionary<string, string> Targets { get; }
    /// <summary>
    /// The assemblies a package / source target of the set provides (by assembly base name).
    /// Dependents reference these instead of the install's dll.
    /// </summary>
    public IReadOnlyDictionary<string, Provider> Providers { get; }

    public sealed record Provider(string Target, TargetKind Kind, string OutName, PackageSpec? Package);

    public TargetSet(string dir)
    {
        Dir = Path.GetFullPath(dir);
        if (!Directory.Exists(Dir)) throw new ArgumentException($"target set not found: {Dir}");
        Targets = VisibleTargets();
        if (Targets.Count == 0) throw new ArgumentException($"no targets in {Dir}");
        Providers = IndexProviders();
    }

    /// <summary>The set's directory: <c>--targets &lt;dir&gt;</c>, else <c>&lt;repo&gt;/targets/&lt;name&gt;</c>.</summary>
    public static TargetSet Open(string? targetsDir, string setName)
    {
        if (targetsDir != null) return new TargetSet(targetsDir);
        string root = RepoRoot()
            ?? throw new ArgumentException($"repo root not found (no Resonator.slnx above {AppContext.BaseDirectory}); pass --targets <dir>");
        return new TargetSet(Path.Combine(root, "targets", setName));
    }

    /// <summary>The repo root: the first ancestor of the running tool that holds Resonator.slnx.</summary>
    static string? RepoRoot()
    {
        for (string? d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar)))
            if (File.Exists(Path.Combine(d, "Resonator.slnx"))) return d;
        return null;
    }

    public TargetSpec Spec(string target) => TargetSpecLoader.Load(Targets[target]);

    /// <summary>True when the target dir belongs to an ancestor set, not to this one.</summary>
    public bool IsInherited(string targetDir) =>
        !Path.GetDirectoryName(Path.GetFullPath(targetDir))!.Equals(Dir, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The targets of a run, in run order: <paramref name="filter"/> = a target name, an assembly
    /// (all its targets), or null (every target). Source targets come first - their dependents'
    /// csprojs reference the projects they produce.
    /// </summary>
    public List<string> Select(string? filter)
    {
        var all = Targets.Keys.ToList();
        var selected = filter == null ? all : Filter(all, filter.TrimEnd('/'));
        return selected
            .OrderBy(t => Providers.Values.Any(p => p.Kind == TargetKind.Source && p.Target.Equals(t, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenBy(t => t, StringComparer.Ordinal)
            .ToList();
    }

    List<string> Filter(List<string> all, string filter)
    {
        if (Targets.ContainsKey(filter)) return [all.First(n => n.Equals(filter, StringComparison.OrdinalIgnoreCase))];

        string assembly = filter.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? filter : filter + ".dll";
        var ofAssembly = all.Where(n => AssemblyOf(n)?.Equals(assembly, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (ofAssembly.Count == 0)
            throw new ArgumentException($"'{filter}' is neither a target in {Dir} nor an assembly with targets there");
        return ofAssembly;
    }

    string? AssemblyOf(string target)
    {
        try { return TargetSpecLoader.RawValue(Targets[target], "assembly"); }
        catch (Exception) { return null; }   // a broken spec is reported when the target itself runs
    }

    Dictionary<string, string> VisibleTargets()
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string set in TargetSpecLoader.SetChain(Dir))
            foreach (string dir in Directory.EnumerateDirectories(set).Where(d => File.Exists(Path.Combine(d, "target.json"))))
                targets[Path.GetFileName(dir)] = Path.GetFullPath(dir);

        // an inherited target that a visible one extends is replaced by it
        var extended = targets.Values.SelectMany(TargetSpecLoader.Ancestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dir) in targets.Where(t => extended.Contains(t.Value) && IsInherited(t.Value)).ToList())
            targets.Remove(name);
        return targets;
    }

    Dictionary<string, Provider> IndexProviders()
    {
        var providers = new Dictionary<string, Provider>(StringComparer.OrdinalIgnoreCase);
        foreach (string target in Targets.Keys)
        {
            TargetSpec spec;
            try { spec = Spec(target); }
            catch (Exception) { continue; }   // a broken spec is reported when the target itself runs
            if (spec.Kind != TargetKind.Decompile && !spec.Tree)   // a tree builds no assembly of the set's
                providers[spec.BaseName] = new Provider(spec.Name, spec.Kind, spec.OutName, spec.Package);
        }
        return providers;
    }
}
