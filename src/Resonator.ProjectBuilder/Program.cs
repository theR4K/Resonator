// Resonator.ProjectBuilder - turns shipped Resonite assemblies into buildable C# projects.
//
//   <shipped.dll> --prepare--> <source.dll> --decompile--> .cs --fixup--> --build--> <rebuilt.dll>
//
// A target's project: the source tree (decompiled, fixed, patched), references.targets (the
// generated reference set) and <name>.csproj (the template - unless --no-csproj).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Resonator.Pipeline;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Stages;
using Resonator.Pipeline.Targets;
using Resonator.ProjectBuilder.PostBuild;

namespace Resonator.ProjectBuilder;

internal static class Program
{
    static readonly string Usage = $"""
        Resonator.ProjectBuilder <output-dir> [options]
        {PipelineSettings.OptionsUsage}
          [--stage all|prepare|decompile|fixup|build]   run one stage only (default: all)
          [--no-csproj]                   emit only source + references.targets (the consumer builds it)
          [--keep-going]                  carry on after a failing target, report all at the end
          [--publish <dir>]               where a successful full run collects the drop-in files
                                          (default: <output-dir>/publish)
          [--no-publish]                  skip the publish step

        One target in the run -> its project goes straight into <output-dir>; several -> <output-dir>/<target>/.

        Target kinds (spec): a decompile of the shipped dll (default), "package" (an official NuGet
        package - nothing is built, dependents get a PackageReference) and "source" (an official git
        repo / archive at a pinned ref - checked out instead of decompiled, then patched).

        Resonator.ProjectBuilder calli <dll> <attribute> [remove-type attribute]
          the post-compile step of a "calli" target (run by its references.targets)
        """;

    enum Stage { All, Prepare, Decompile, Fixup, Build }

    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }
        try
        {
            if (args[0] == "calli") return Calli(args[1..]);
            var settings = PipelineSettings.Parse(args[0], args[1..], out var rest);
            var (stage, keepGoing, noCsproj, publish) = ParseOwnOptions(rest);
            settings = settings with { NoCsproj = noCsproj };
            return Build(settings, stage, keepGoing, publish == "" ? null : publish ?? Path.Combine(settings.OutputDir, "publish"));
        }
        catch (Exception e)
        {
            Console.WriteLine("FAIL: " + e.Message);
            return 1;
        }
    }

    /// <param name="publishDir">null: no publish step (--no-publish)</param>
    static int Build(PipelineSettings settings, Stage stage, bool keepGoing, string? publishDir)
    {
        var selected = settings.Set.Select(settings.TargetFilter);
        var targets = ReferencesTargets.WithSourceTargets(settings, selected);
        Log.Info("=== Resonator.ProjectBuilder");
        Log.Info($"  output  : {settings.OutputDir}");
        Log.Info($"  targets : {(targets.Count == 1 ? targets[0] : targets.Count + " targets")}");
        if (targets.Count > selected.Count)
            Log.Info($"  + the source target(s) they reference: {string.Join(", ", targets.Except(selected))}");
        Log.Info($"  set     : {settings.Set.Dir}");
        Log.Info($"  stage   : {stage.ToString().ToLowerInvariant()}   pdb: {(settings.UsePdb ? "on" : "off")}   csproj: {(settings.NoCsproj ? "off" : "on")}");

        bool Runs(Stage s) => stage == Stage.All || stage == s;
        var failed = new List<string>();
        var built = new List<Target>();
        foreach (string name in targets)
        {
            try
            {
                var target = Target.InRun(settings, name, targets.Count);
                Log.Info();
                if (target.Spec.Package is { } package)
                {
                    Log.Info($"=== {target.Name}: NuGet package {package.Id} {package.Version} - nothing to build (dependents reference the package)");
                    continue;
                }
                string from = target.Spec.Kind == TargetKind.Source ? "source" : "weave: " + target.Spec.Weave.ToString().ToLowerInvariant();
                Log.Info($"=== {target.Name}  ->  {target.ProjectDir}   ({from}, patch layers: {target.Spec.PatchDirs.Count})");

                if (Runs(Stage.Prepare) || Runs(Stage.Decompile)) PrepareStage.Run(target);   // decompile needs it; cached
                if (Runs(Stage.Decompile)) DecompileStage.Run(target);
                if (Runs(Stage.Fixup)) FixupStage.Run(target);
                if (Runs(Stage.Build)) BuildStage.Run(target);
                if (Runs(Stage.Build) && target.OutDll != null && File.Exists(target.OutDll)) built.Add(target);
            }
            catch (Exception e) when (keepGoing)
            {
                Log.Info($"FAIL ({name}): {e.Message}");
                failed.Add(name);
            }
        }

        Log.Info();
        if (failed.Count > 0)
        {
            Log.Info("PIPELINE FAILED: " + string.Join(", ", failed));
            return 1;
        }
        // the drop-in folder needs every target of the run built: a full run that produced projects
        if (publishDir != null && stage == Stage.All && !settings.NoCsproj && built.Count > 0)
        {
            PublishStage.Run(settings, built, Path.GetFullPath(publishDir));
        }
        Log.Info();
        Log.Info("PIPELINE OK");
        return 0;
    }

    /// <returns>Publish: the --publish dir, "" for --no-publish, null for the default</returns>
    static (Stage Stage, bool KeepGoing, bool NoCsproj, string? Publish) ParseOwnOptions(List<string> args)
    {
        var stage = Stage.All;
        bool keepGoing = false, noCsproj = false;
        string? publish = null;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--stage":
                    string value = ++i < args.Count ? args[i] : throw new ArgumentException("--stage needs a value");
                    if (!Enum.TryParse(value, ignoreCase: true, out stage) || int.TryParse(value, out _))
                        throw new ArgumentException($"unknown stage '{value}' (expected all|prepare|decompile|fixup|build)");
                    break;
                case "--keep-going": keepGoing = true; break;
                case "--no-csproj": noCsproj = true; break;
                case "--publish": publish = ++i < args.Count ? args[i] : throw new ArgumentException("--publish needs a value"); break;
                case "--no-publish": publish = ""; break;
                default: throw new ArgumentException($"unknown option '{args[i]}'\n\n{Usage}");
            }
        }
        return (stage, keepGoing, noCsproj, publish);
    }

    static int Calli(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("usage: Resonator.ProjectBuilder calli <dll> <attribute> [remove-type attribute]");
        CalliWeaver.Run(args[0], args[1], args.Length > 2 ? args[2] : null, Console.Out);
        return 0;
    }
}
