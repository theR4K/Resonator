using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;

namespace Resonator.Pipeline.Stages;

/// <summary>
/// Stage 2: the target's C# source tree. Decompiled from the prepared dll - or, for a source
/// target, checked out from the official source. Regeneration starts fresh but keeps hand-written
/// code (keep dirs, e.g. Injected/): the project dir may hold a consumer's files.
/// </summary>
public static class DecompileStage
{
    public static void Run(Target target)
    {
        if (target.Spec.Kind == TargetKind.Source)
        {
            SourceCheckout.Run(target);
            return;
        }
        if (!File.Exists(target.SourceDll))
            throw new Exception($"source dll missing ({(target.Spec.Weave == WeaveMode.None ? "shipped dll not found" : "prepare stage not run")}): {target.SourceDll}");

        var previous = SourceTree.GeneratedFiles(target.SrcDir, target.Spec.KeepDirs);
        Directory.CreateDirectory(target.SrcDir);
        if (target.Spec.SelectTypes.Length > 0)
            Decompiler.Types(target);
        else
        {
            Decompiler.WholeProject(target);
            RemoveExcludedTypes(target);
        }

        int stale = SourceTree.DeleteStale(previous, SourceTree.GeneratedFiles(target.SrcDir, target.Spec.KeepDirs));
        if (stale > 0) Log.Info($"  dropped {stale} stale source file(s)");
    }

    /// <summary>
    /// exclude_types: decompile everything EXCEPT these - the types the split-out targets claim.
    /// Each entry maps to its top-level type's file; deleting the file removes the type with its
    /// nested types. Unlike a deletion patch this needs no content match, so it cannot drift.
    /// </summary>
    static void RemoveExcludedTypes(Target target)
    {
        var excluded = target.Spec.ExcludeTypes;
        if (excluded.Length == 0) return;
        int removed = 0;
        foreach (string entry in excluded)
        {
            string? file = FileOfType(target.SrcDir, entry);
            if (file == null)
            {
                Log.Info($"  excluded: {entry} (not in source - nothing to remove)");
                continue;
            }
            File.Delete(file);
            removed++;
            Log.Info("  excluded: " + Path.GetRelativePath(target.SrcDir, file));
        }
        Log.Info($"excluded {removed} type file(s) from the decompilation");
    }

    /// <summary>
    /// The file declaring <paramref name="entry"/> (Namespace.Type, or Namespace.Type/Nested -> the
    /// outer type's file); null if the type is not in the tree. The decompiler mirrors the original
    /// repo layout (PDB), so the folder is not derivable from the namespace: search by file name,
    /// then disambiguate by the innermost namespace segment as a folder.
    /// </summary>
    static string? FileOfType(string srcDir, string entry)
    {
        var segments = entry.Split('/')[0].Split('.');
        string type = segments[^1];
        string? ns = segments.Length > 1 ? segments[^2] : null;

        var candidates = SourceTree.CsFiles(srcDir, type + ".cs").ToList();
        if (candidates.Count == 0)
        {
            // compiler-generated types get a sanitized FILE name (<>z__List -> --z__List.cs): find the declaration
            var declaration = new Regex(@"\b(?:class|struct|interface|enum)\s+" + Regex.Escape(type) + @"\b");
            candidates = SourceTree.CsFiles(srcDir).Where(f => declaration.IsMatch(File.ReadAllText(f))).ToList();
            if (candidates.Count == 0) return null;
        }
        if (candidates.Count > 1 && ns != null)
            candidates = candidates.Where(f => Path.GetRelativePath(srcDir, f).Split(Path.DirectorySeparatorChar, '/')
                .Any(folder => folder.Equals(ns, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count != 1)
            throw new Exception($"exclude_types: '{entry}' is ambiguous ({candidates.Count} files: " +
                                string.Join(", ", candidates.Select(f => Path.GetRelativePath(srcDir, f))) + ")");
        return candidates[0];
    }
}
