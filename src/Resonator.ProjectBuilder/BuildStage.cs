using System;
using System.IO;
using System.Linq;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;
using Resonator.ProjectBuilder.BuildFixes;
using Resonator.ProjectBuilder.PostBuild;
using Resonator.Weaver.Checks;

namespace Resonator.ProjectBuilder;

/// <summary>
/// Stage 4: compile the project. Each failing pass is handed to the build-error fixes; the build
/// is retried while they find something to fix. Then the post-build steps: ctor stripping
/// (spec fix_ctor_types) and, for a flattened build, the sync-member consistency check.
/// </summary>
internal static class BuildStage
{
    const int MaxPasses = 12;

    static readonly IBuildErrorFix[] Fixes = [new MissingReferenceFix(), new RefArgumentFix()];

    public static void Run(Target target)
    {
        if (target.Csproj == null || target.OutDll == null)
        {
            Log.Info();
            Log.Info("build: skipped - source-only mode (no csproj; the consumer builds it)");
            return;
        }
        CsprojTemplate.Deploy(target, target.Csproj);
        Compile(target, target.Csproj);
        if (!File.Exists(target.OutDll)) throw new Exception("expected output not found: " + target.OutDll);
        Log.Info("BUILD OK -> " + target.OutDll);

        // The compiler always emits a default .ctor; in the original a weaver stripped it from the
        // Fody marker types. Removing it makes the rebuilt dll match, with no source change.
        if (target.Spec.FixCtorTypes.Length > 0)
        {
            Log.Banner("FIXCTOR (strip ctor: " + string.Join(", ", target.Spec.FixCtorTypes) + ")");
            FixCtor.Run(target.OutDll, target.Spec.FixCtorTypes, Console.Out);
        }

        // A flattened build is never reweaved: its generated sync-member switches are final, so a
        // patch that adds a sync field or Worker must keep them consistent.
        if (target.Spec.Weave == WeaveMode.Flatten)
        {
            Log.Banner("SYNCMEMBERS (generated GetSyncMember consistency)");
            if (SyncMemberCheck.Run(target.OutDll, Console.Out, [target.Settings.ResoniteDir]) != 0)
                throw new Exception("syncmembers check failed (see above)");
        }
    }

    static void Compile(Target target, string csproj)
    {
        for (int pass = 1; ; pass++)
        {
            var (exitCode, log) = Shell.Capture("dotnet", "build", csproj, "-c", "Release", "--nologo", "-v", "quiet");
            foreach (string line in log.Split('\n').Where(l => l.Contains(" error ")))
                Log.Info("    " + line.Trim());
            if (exitCode == 0) return;

            int fixedCount = Fixes.Sum(fix => fix.Apply(target, log));
            Log.Info($"build pass {pass}/{MaxPasses} exit={exitCode} auto-fixed={fixedCount}");
            if (fixedCount == 0) throw new Exception("build failed with no auto-fixes available (see errors above)");
            if (pass == MaxPasses) return;   // the output check reports it
        }
    }
}
