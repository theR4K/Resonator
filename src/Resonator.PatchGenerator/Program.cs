// Resonator.PatchGenerator - turns your edits of a built project back into the target set's patches.
//
// Edit the project ProjectBuilder produced, then run this over the same dir: every difference from a
// fresh pipeline output becomes (or stops being) a patch of the target's own layer. Commit the set.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Resonator.Pipeline;
using Resonator.Pipeline.Stages;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;

namespace Resonator.PatchGenerator;

internal static class Program
{
    static readonly string Usage = $"""
        Resonator.PatchGenerator <project-dir> [options] [--no-csproj]
        {PipelineSettings.OptionsUsage}
        <project-dir>: the output dir of a ProjectBuilder run (same set and target selection), edited.
        --no-csproj: that run was --no-csproj (its layout leaves out the referenced source targets).

        Patches are written only into the set being worked on: the edits of an inherited target
        become a new layer here (a spec that extends the inherited one).
        """;

    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }
        try
        {
            var settings = PipelineSettings.Parse(args[0], args[1..], out var rest);
            // the ProjectBuilder run's --no-csproj: it leaves out the referenced source targets, so a single target's tree
            // is the dir itself (without it, a target with source references was looked for in <dir>/<target>/ and every
            // file read as deleted)
            if (rest.Remove("--no-csproj")) settings = settings with { NoCsproj = true };
            if (rest.Count > 0) throw new ArgumentException($"unknown option '{rest[0]}'\n\n{Usage}");

            var targets = settings.Set.Select(settings.TargetFilter);
            // the layout of the ProjectBuilder run that made the output (it adds referenced source targets)
            int runCount = ReferencesTargets.WithSourceTargets(settings, targets).Count;
            foreach (string name in targets)
            {
                var target = Target.InRun(settings, name, runCount);
                if (target.Spec.Kind == TargetKind.Package) continue;   // a NuGet package has no source to patch
                Generate(target);
            }
            Log.Info();
            Log.Info("SYNC OK - the target set's patches now match the working output. Commit the set.");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("FAIL: " + e.Message);
            return 1;
        }
    }

    static void Generate(Target target)
    {
        var set = target.Settings.Set;
        string? inherited = set.IsInherited(target.Spec.Dir) ? target.Spec.Dir : null;
        if (inherited != null)
        {
            // a new layer in this set: the inherited layers stay the baseline, the edits go here
            string layer = Path.Combine(set.Dir, Path.GetFileName(inherited));
            target = target with { Spec = target.Spec with { Dir = layer, PatchDirs = [.. target.Spec.PatchDirs, Path.Combine(layer, "patches")] } };
            Log.Info();
            Log.Info($"=== {target.Name} is inherited from {inherited} - new patches go to {layer}");
        }

        Log.Info();
        Log.Info("=== SYNC " + target.Name);
        Log.Info($"  working : {target.ProjectDir}");
        Log.Info($"  patches : {target.Spec.PatchDir}");
        PatchGenerator.Run(target);
        if (inherited != null) KeepLayer(target.Spec.Dir, inherited);
    }

    /// <summary>
    /// The new layer of an inherited target stays only if patches were written into it; it is then
    /// a spec extending the inherited one ("&lt;Set&gt;/&lt;Target&gt;").
    /// </summary>
    static void KeepLayer(string layerDir, string inheritedDir)
    {
        string patches = Path.Combine(layerDir, "patches");
        if (!Directory.Exists(patches) || !Directory.EnumerateFiles(patches, "*.patch").Any())
        {
            if (Directory.Exists(layerDir)) Directory.Delete(layerDir, recursive: true);
            return;
        }
        string setsRoot = Path.GetDirectoryName(Path.GetDirectoryName(inheritedDir)!)!;
        string extends = Path.GetRelativePath(setsRoot, inheritedDir).Replace(Path.DirectorySeparatorChar, '/');
        var spec = new JsonObject { ["extends"] = extends };
        File.WriteAllText(Path.Combine(layerDir, "target.json"), spec.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Log.Info($"new layer: {layerDir} (extends {extends})");
    }
}
