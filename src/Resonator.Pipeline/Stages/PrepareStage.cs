using System;
using System.IO;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;
using Resonator.Weaver.Checks;
using Resonator.Weaver.Deweaving;
using Resonator.Weaver.Flattening;

namespace Resonator.Pipeline.Stages;

/// <summary>
/// Stage 1: turn the shipped dll into the dll the decompiler reads (spec "weave"):
///   deweave  revert the weave exactly - the pre-weave original
///   flatten  keep the weave, fix only what C# cannot express - no reweave needed
///   none     not weaver-processed: the shipped dll is decompiled as is
/// Prepared dlls are cached per shipped dll (the split targets of one assembly share one).
/// </summary>
public static class PrepareStage
{
    public static void Run(Target target)
    {
        var weave = target.Spec.Weave;
        if (weave == WeaveMode.None)
        {
            Log.Info($"=== PREPARE (none: target '{target.Spec.BaseName}' is not weaver-processed - decompiling the shipped dll directly)");
            return;
        }
        string shipped = target.ShippedDll;
        if (!File.Exists(shipped)) throw new Exception("shipped dll not found: " + shipped);

        // the cache stamp: what was prepared, from which file
        string stampFile = target.SourceDll + ".source";
        string stamp = $"{Name(weave)}{(weave == WeaveMode.Flatten ? "/v" + Flattener.Version : "")}|{shipped}|{new FileInfo(shipped).Length}|{File.GetLastWriteTimeUtc(shipped).Ticks}";
        if (File.Exists(target.SourceDll) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp)
        {
            Log.Info($"=== PREPARE {Name(weave)} (cached: {target.SourceDll})");
            return;
        }
        if (File.Exists(stampFile)) File.Delete(stampFile);

        if (target.Spec.WeaverCheck != null) RequireKnownWeaver(target);
        if (weave == WeaveMode.Flatten) Flatten(target);
        else Deweave(target);
        File.WriteAllText(stampFile, stamp);
    }

    /// <summary>
    /// Both modes rely on the shape of ONE specific weaver: deweave is its exact inverse, flatten's
    /// fixes assume its output. An unknown weaver is refused rather than risk a silently wrong dll.
    /// </summary>
    static void RequireKnownWeaver(Target target)
    {
        string weaver = target.Spec.WeaverCheck!;
        Log.Banner($"KNOWN-WEAVER CHECK ({weaver})");
        var report = KnownWeaverCheck.Run(Path.GetFullPath(Path.Combine(target.Settings.ResoniteDir, weaver)));
        Log.Info(report.ToString());
        if (!report.Known)
            throw new Exception(
                $"shipped weaver is NOT a known FrooxEngine.Weaver — the {Name(target.Spec.Weave)} may not match it. " +
                "Refusing. (Update Resonator.Weaver for this weaver version, or point 'weaver_check' at a known weaver.)");
    }

    static void Deweave(Target target)
    {
        Log.Banner("DEWEAVE");
        var report = Deweaver.Deweave(target.ShippedDll, new DeweaveOptions { OutputDir = target.PreparedDir });
        Log.Info(report.ToString());
        if (report.Error != null) throw new Exception("deweave failed: " + report.Error);
        if (!report.WasWeaved) throw new Exception("deweave: input was not weaved (no FROOXENGINE_WEAVED marker) — nothing to revert");

        Log.Banner("VERIFY deweaved");
        if (ResidueCheck.Run(target.SourceDll, Console.Out) != 0) throw new Exception("deweaved dll failed the residue verify");
    }

    static void Flatten(Target target)
    {
        Log.Banner("FLATTEN (keep the weave; fix C#-inexpressible IL)");
        var report = Flattener.Flatten(target.ShippedDll, target.PreparedDir);
        Log.Info(report.ToString());
        if (report.Error != null) throw new Exception("flatten failed: " + report.Error);
        if (!report.WasWeaved) throw new Exception("flatten: input was not weaved (no FROOXENGINE_WEAVED marker) — use weave: none");
    }

    static string Name(WeaveMode weave) => weave.ToString().ToLowerInvariant();
}
