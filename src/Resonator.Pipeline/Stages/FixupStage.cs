using System;
using System.Linq;
using Resonator.Pipeline.Fixups;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Patching;
using Resonator.Pipeline.Targets;

namespace Resonator.Pipeline.Stages;

/// <summary>
/// Stage 3: make the source tree compile. Writes references.targets, repairs the decompiler's
/// mechanical artifacts, then applies the target's patch layers. Official source (a source
/// target) has no decompiler artifacts: only the patches. A tree target (no C# project of
/// Resonator's) gets no references.targets either.
/// </summary>
public static class FixupStage
{
    static readonly ISourceFix[] DecompilerFixes =
    [
        new OpenGenericCastFix(),
        new UnsafeAsFix(),
        new NewTypeArgumentFix(),
        new GenericMemberCallFix(),
    ];

    public static void Run(Target target)
    {
        var files = SourceTree.Files(target.SrcDir, target.Spec.FilePatterns).ToList();
        if (files.Count == 0) throw new Exception($"no source files - run stage 'decompile' first ({target.SrcDir})");

        if (target.SkipReferences) Log.Info("references: skipped (baseline mode)");
        else if (target.Spec.Tree) Log.Info("references: none (tree target: the consumer's own build)");
        else ReferencesTargets.Write(target);

        if (target.Spec.Kind == TargetKind.Source)
        {
            PatchLayers.Apply(target);
            Log.Info("fixup done (source target: patches only)");
            return;
        }
        var counts = DecompilerFixes.Select(fix => $"{fix.Name}={fix.Apply(files)}").ToList();
        PatchLayers.Apply(target);
        Log.Info("fixup done: " + string.Join(" ", counts));
    }
}
