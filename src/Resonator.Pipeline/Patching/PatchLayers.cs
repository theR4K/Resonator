using System;
using System.IO;
using System.Linq;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;

namespace Resonator.Pipeline.Patching;

/// <summary>
/// Applies a target's patch layers: every *.patch of every layer, root ancestor first, the
/// target's own patches/ last - a layer's patches apply on top of its parent's. Directory-driven:
/// a patch file works the moment it is dropped in.
/// </summary>
public static class PatchLayers
{
    public static void Apply(Target target)
    {
        int applied = 0, already = 0, failed = 0, outOfScope = 0;
        foreach (string dir in target.Spec.PatchDirs)
        {
            string[] patchFiles = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.patch").OrderBy(f => f, StringComparer.Ordinal).ToArray()
                : [];
            Log.Info();
            Log.Info($"=== PATCHES ({(patchFiles.Length == 0 ? "none in " : "")}{dir})");
            foreach (string file in patchFiles)
            {
                var patch = Patch.Parse(File.ReadAllText(file));
                // a type-subset target inherits its parent's patches; those for files outside its selection are not its own
                if (target.Spec.SelectTypes.Length > 0 && !patch.IsDeletion && !File.Exists(Path.Combine(target.SrcDir, patch.FilePath)))
                {
                    outOfScope++;
                    continue;
                }
                switch (ApplyOne(target, patch, Path.GetFileName(file)))
                {
                    case PatchResult.Applied: applied++; break;
                    case PatchResult.AlreadyApplied: already++; break;
                    default: failed++; break;
                }
            }
        }
        Log.Info($"patches: applied={applied} already={already} skipped={failed}"
            + (outOfScope > 0 ? $" out-of-scope={outOfScope} (files outside select_types)" : ""));
    }

    /// <summary>A patch that does not apply is reported, not fatal: if it mattered, the build says so.</summary>
    static PatchResult? ApplyOne(Target target, Patch patch, string name)
    {
        string file = patch.FilePath.Replace('/', Path.DirectorySeparatorChar);
        try
        {
            var result = PatchApplier.Apply(target.SrcDir, patch);
            Log.Info(result == PatchResult.Applied ? $"patched : {file}  [{name}]" : $"patch ALREADY: {file}  [{name}]");
            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"patch ERROR: {file} [{name}] {ex.Message}");
            return null;
        }
    }
}
