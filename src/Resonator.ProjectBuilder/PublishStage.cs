using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Resonator.Pipeline;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;

namespace Resonator.ProjectBuilder;

/// <summary>
/// After a successful build run: one drop-in folder to copy over a game install. Every built
/// target's bin is overlaid on the install's layout, and only what changes it is kept:
///   - the run's own outputs (dll + pdb) and the set's NuGet package assemblies: whenever they
///     differ from the install's copy - the target's own bin wins over copies in its dependents' bins
///   - any other top-level file the install does not have (a package a target adds: Concentus)
///   - under runtimes/ (and other subfolders): only paths the install already has, when they differ
///     (a replacing package's natives) - never the extra platforms a package brings along
/// Not published: a file the install has that the run does not own (a package's older copy of a
/// shipped dependency - the game's stays), and the per-library *.deps.json / *.runtimeconfig.json.
/// publish.txt lists each file and the target it came from.
/// </summary>
internal static class PublishStage
{
    const string Manifest = "publish.txt";

    public static void Run(PipelineSettings settings, IReadOnlyList<Target> built, string dir)
    {
        Log.Banner("PUBLISH -> " + dir);
        string install = settings.ResoniteDir;
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in built)
        {
            owned.Add(target.Spec.OutName + ".dll");
            owned.Add(target.Spec.OutName + ".pdb");
        }
        foreach (var (assembly, provider) in settings.Set.Providers)
            if (provider.Package != null) owned.Add(assembly + ".dll");

        // relative path -> (file, target); a target's own outputs first, so they win over copies
        var files = new Dictionary<string, (string File, string Target)>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in built)
        {
            string bin = Path.GetDirectoryName(target.OutDll!)!;
            foreach (string own in new[] { target.Spec.OutName + ".dll", target.Spec.OutName + ".pdb" })
                if (File.Exists(Path.Combine(bin, own))) files.TryAdd(own, (Path.Combine(bin, own), target.Name));
        }
        foreach (var target in built)
        {
            string bin = Path.GetDirectoryName(target.OutDll!)!;
            foreach (string file in Directory.EnumerateFiles(bin, "*", SearchOption.AllDirectories))
                files.TryAdd(Path.GetRelativePath(bin, file), (file, target.Name));
        }

        var published = new SortedDictionary<string, (string File, string Target)>(StringComparer.OrdinalIgnoreCase);
        int keptGame = 0;
        foreach (var (rel, source) in files)
        {
            if (rel.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                rel.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) continue;
            bool topLevel = Path.GetDirectoryName(rel) is null or "";
            string shipped = Path.Combine(install, rel);
            if (!File.Exists(shipped))
            {
                if (topLevel) published[rel] = source;
            }
            else if (!SameContent(source.File, shipped))
            {
                if (!topLevel || owned.Contains(rel)) published[rel] = source;
                else keptGame++;
            }
        }

        Prepare(dir);
        foreach (var (rel, source) in published)
        {
            string to = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(source.File, to, overwrite: true);
        }
        File.WriteAllLines(Path.Combine(dir, Manifest),
            published.Select(p => $"{p.Key.Replace('\\', '/')}  <-  {p.Value.Target}"));
        Log.Info($"published {published.Count} file(s) from {built.Count} target(s); " +
                 $"{keptGame} differing copy(ies) of game files not owned by the run left out");
        Log.Info($"copy {dir}{Path.DirectorySeparatorChar}* over a game install (not the one the pipeline reads)");
    }

    /// <summary>The folder is the tool's: emptied when it holds a previous publish (or nothing), refused otherwise.</summary>
    static void Prepare(string dir)
    {
        if (Directory.Exists(dir))
        {
            bool ours = File.Exists(Path.Combine(dir, Manifest)) || !Directory.EnumerateFileSystemEntries(dir).Any();
            if (!ours) throw new Exception($"publish folder {dir} is not empty and holds no {Manifest} - not overwriting it");
            Directory.Delete(dir, recursive: true);
        }
        Directory.CreateDirectory(dir);
    }

    static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }
}
