using System.IO;

namespace Resonator.Pipeline.Targets;

/// <summary>One target of a run: its spec, the run's settings, and every path the stages use.</summary>
public sealed record Target(PipelineSettings Settings, TargetSpec Spec, string ProjectDir)
{
    /// <summary>
    /// The target in a run of <paramref name="runCount"/> targets: one target -> its project goes
    /// straight into the output dir; several -> &lt;output-dir&gt;/&lt;target&gt;/.
    /// </summary>
    public static Target InRun(PipelineSettings settings, string target, int runCount)
    {
        var spec = settings.Set.Spec(target);
        return new Target(settings, spec, runCount == 1 ? settings.OutputDir : Path.Combine(settings.OutputDir, spec.Name));
    }

    public string Name => Spec.Name;

    /// <summary>Generate the source somewhere other than the project dir (a sync baseline).</summary>
    public string? SourceRootOverride { get; init; }
    /// <summary>Do not write references.targets (a sync baseline has no use for it).</summary>
    public bool SkipReferences { get; init; }

    /// <summary>The generated source root.</summary>
    public string SrcDir => SourceRootOverride ?? ProjectDir;

    /// <summary>The dll in the install: flat layout, else the nested plugin layout.</summary>
    public string ShippedDll
    {
        get
        {
            string flat = Path.Combine(Settings.ResoniteDir, Spec.Assembly);
            return File.Exists(flat) ? flat : Path.Combine(Settings.ResoniteDir, "Resonite", "Plugins", Spec.BaseName, Spec.BaseName, Spec.Assembly);
        }
    }

    /// <summary>The dll the decompiler reads: the prepared one, or the shipped one for weave none.</summary>
    public string SourceDll => Spec.Weave == WeaveMode.None ? ShippedDll : Path.Combine(PreparedDir, Spec.Assembly);

    public string PreparedDir => Path.Combine(Settings.WorkDir, Spec.Weave == WeaveMode.Flatten ? "flattened" : "deweaved");

    public string? Csproj => Settings.NoCsproj ? null : Path.Combine(ProjectDir, Spec.OutName + ".csproj");

    public string? OutDll => Csproj == null ? null : Path.Combine(ProjectDir, "bin", "Release", Spec.Tfm, Spec.OutName + ".dll");

    public string ReferencesTargets => Path.Combine(ProjectDir, "references.targets");
}
