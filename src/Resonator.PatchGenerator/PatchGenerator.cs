using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Patching;
using Resonator.Pipeline.Stages;
using Resonator.Pipeline.Targets;

namespace Resonator.PatchGenerator;

/// <summary>
/// Regenerates a target's own patch layer from its WORKING source tree. The working tree is the
/// truth: whatever differs from a fresh pipeline output becomes (or stops being) a patch.
///
///   baseline = prepare -> decompile -> fixup with only the ANCESTOR layers' patches (under the
///              work dir, disposable) - so a layer's patches are deltas over its parent's, in the
///              order the build applies them
///   working  = the project dir of a previous build, edited
///
///   same in both                -> no patch (a stale one is dropped)
///   changed                     -> patches/&lt;file&gt;.patch           (unified diff)
///   in baseline, not in working -> patches/&lt;file&gt;.deletion.patch
///   in working, not in baseline -> an INJECTION, reported: hand-written code belongs in a keep dir
///                                  (Injected/) or in the consumer repo
///
/// The baseline applies the spec's type scope too, so only this target's files are ever compared.
/// Drift it does not recognize is yours to resolve - it never guesses.
/// </summary>
internal static class PatchGenerator
{
    public static void Run(Target target)
    {
        string working = target.SrcDir;
        if (!Directory.Exists(working) || !SourceTree.Files(working, target.Spec.FilePatterns).Any())
            throw new Exception($"working source missing/empty: {working}\n  build the project first (Resonator.ProjectBuilder)");

        var baseline = target with
        {
            Spec = target.Spec with { PatchDirs = target.Spec.PatchDirs.SkipLast(1).ToList(), WeaverCheck = null },   // checked by the working run
            SourceRootOverride = Path.Combine(target.Settings.WorkDir, target.Name + "-baseline"),
            SkipReferences = true,
        };
        PrepareStage.Run(target);   // cached by the working run
        Log.Banner($"BASELINE (fresh decompile, {baseline.Spec.PatchDirs.Count} ancestor patch layer(s)) -> {baseline.SrcDir}");
        DecompileStage.Run(baseline);
        FixupStage.Run(baseline);

        Log.Banner("PATCH-SYNC (working vs baseline) -> " + target.Spec.PatchDir);
        Emit(target, baseline.SrcDir, working);
    }

    static void Emit(Target target, string baseline, string working)
    {
        string patchDir = target.Spec.PatchDir;
        Directory.CreateDirectory(patchDir);
        var baseFiles = RelativeFiles(baseline, target.Spec.FilePatterns);
        var workFiles = RelativeFiles(working, target.Spec.FilePatterns, target.Spec.KeepDirs);   // keep dirs (Injected/) are hand-written by design: no patches, no injection warnings
        // every existing patch is stale until this run writes it again
        var stale = Directory.EnumerateFiles(patchDir, "*.patch").ToHashSet(StringComparer.OrdinalIgnoreCase);
        // patches/ is flat: a file name shared by several source files (FrooxEngine/Slider.cs and
        // FrooxEngine/UIX/Slider.cs) gets its folder in the patch name, or the two would overwrite each other
        var shared = baseFiles.Keys.GroupBy(PatchLeaf, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int written = 0, deletions = 0, unchanged = 0;
        foreach (var (rel, baseFile) in baseFiles.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string leaf = shared.Contains(PatchLeaf(rel)) ? rel.Replace(Path.DirectorySeparatorChar, '.').Replace('/', '.') : PatchLeaf(rel);
            string? diff, name;
            if (workFiles.TryGetValue(rel, out var workFile))
            {
                string before = File.ReadAllText(baseFile), after = File.ReadAllText(workFile);
                diff = before == after ? "" : ExactDiff(rel, TextLines.Split(before), TextLines.Split(after));
                name = leaf + ".patch";
                if (diff.Length == 0)
                {
                    unchanged++;
                    DropStale(patchDir, stale, leaf);
                    continue;
                }
                written++;
                Log.Info($"patch  : {rel}  [{name}]");
            }
            else
            {
                diff = UnifiedDiff.Deletion(rel, TextLines.Split(File.ReadAllText(baseFile)));
                name = leaf + ".deletion.patch";
                deletions++;
                Log.Info($"delete : {rel}  [{name}]");
            }
            string path = Path.Combine(patchDir, name);
            File.WriteAllText(path, diff);
            stale.Remove(path);
        }

        foreach (string path in stale)
        {
            File.Delete(path);
            Log.Info("dropped stale patch: " + Path.GetFileName(path));
        }

        var injections = workFiles.Keys.Where(k => !baseFiles.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (injections.Count > 0)
        {
            Log.Info();
            Log.Warn("INJECTIONS (present in working src, NOT in the decompiled baseline):\n"
                     + string.Concat(injections.Select(i => "  " + i + "\n"))
                     + "  to survive regeneration, keep hand-written code in a keep dir\n"
                     + $"  ({string.Join(", ", target.Spec.KeepDirs)}/) or in the consumer repo - not in the decompiled tree.");
        }

        Log.Info();
        Log.Info($"patch-sync: {written} patched, {deletions} deleted, {unchanged} unchanged, {injections.Count} injection(s)");
    }

    static string PatchLeaf(string rel) => Path.GetFileName(rel);

    /// <summary>
    /// A diff that the applier turns back into exactly <paramref name="after"/>. The applier finds a hunk by its content, so
    /// identical code in several places (three draw calls with the same lines around the same change) makes identical hunks,
    /// and one could be taken for another that is already applied; more context sets them apart. Up to the whole file.
    /// </summary>
    static string ExactDiff(string rel, List<string> before, List<string> after)
    {
        for (int context = UnifiedDiff.DefaultContext; ; context *= 2)
        {
            string diff = UnifiedDiff.Content(rel, before, after, context);
            var lines = new List<string>(before);
            bool exact;
            try { exact = PatchApplier.ApplyLines(lines, Patch.Parse(diff)) > 0 && lines.SequenceEqual(after, StringComparer.Ordinal); }
            catch (Exception) { exact = false; }
            if (exact) return diff;
            if (context > Math.Max(before.Count, after.Count))
                throw new Exception($"{rel}: no diff reproduces the working file, even with the whole file as context");
            Log.Info($"  {rel}: hunks with {context} lines of context are ambiguous, trying {context * 2}");
        }
    }

    static Dictionary<string, string> RelativeFiles(string root, IReadOnlyList<string> globs, IEnumerable<string>? keepDirs = null) =>
        SourceTree.GeneratedFiles(root, keepDirs ?? [], globs).ToDictionary(f => Path.GetRelativePath(root, f), StringComparer.Ordinal);

    /// <summary>The file is unmodified again: its patch (content or deletion) goes.</summary>
    static void DropStale(string patchDir, HashSet<string> stale, string leaf)
    {
        foreach (string name in new[] { leaf + ".patch", leaf + ".deletion.patch" })
        {
            string path = Path.Combine(patchDir, name);
            if (!stale.Remove(path) || !File.Exists(path)) continue;
            File.Delete(path);
            Log.Info("dropped stale patch (file now unmodified): " + name);
        }
    }
}
