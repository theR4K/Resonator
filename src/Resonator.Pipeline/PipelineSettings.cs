using System;
using System.Collections.Generic;
using System.IO;
using Resonator.Pipeline.Targets;

namespace Resonator.Pipeline;

/// <summary>The command line both pipeline tools share.</summary>
public sealed record PipelineSettings
{
    public const string OptionsUsage = """
          [--target <target|assembly>]    one target, or every target of an assembly (default: all)
          [--set <name>]                  the target set <repo>/targets/<name> (default: Resonator)
          [--targets <dir>]               an explicit target-set directory (overrides --set)
          [--resonite <dir>]              Resonite install (default: %RESONITE_DIR%, else the Steam path if it exists)
          [--no-pdb]                      decompile without the PDB (decompiler-generated local names)
        """;

    /// <summary>Where the project(s) go: one target -> straight into it; several -> &lt;dir&gt;/&lt;target&gt;/.</summary>
    public required string OutputDir { get; init; }
    public required TargetSet Set { get; init; }
    public string? TargetFilter { get; init; }
    public required string ResoniteDir { get; init; }
    public bool UsePdb { get; init; } = true;
    /// <summary>Emit only source + references.targets: the consumer compiles the tree from its own csproj.</summary>
    public bool NoCsproj { get; init; }
    /// <summary>Scratch space: prepared dlls, fetched sources, sync baselines.</summary>
    public string WorkDir { get; init; } = Path.Combine(Path.GetTempPath(), "resonator");

    /// <summary>
    /// Parse the shared options. Everything else is returned, in order, for the tool's own options.
    /// </summary>
    public static PipelineSettings Parse(string outputDir, string[] args, out List<string> rest)
    {
        string? targets = null, filter = null, resonite = null;
        string set = "Resonator";
        bool usePdb = true;
        rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException(args[i - 1] + " needs a value");
            switch (args[i])
            {
                case "--target": filter = Value(); break;
                case "--targets": targets = Value(); break;
                case "--set": set = Value(); break;
                case "--resonite": resonite = Value(); break;
                case "--no-pdb": usePdb = false; break;
                default: rest.Add(args[i]); break;
            }
        }
        return new PipelineSettings
        {
            OutputDir = Path.GetFullPath(outputDir),
            Set = TargetSet.Open(targets, set),
            TargetFilter = filter,
            ResoniteDir = resonite ?? DefaultResoniteDir(),
            UsePdb = usePdb,
        };
    }

    const string SteamResoniteDir = @"C:\Program Files (x86)\Steam\steamapps\common\Resonite";

    /// <summary>%RESONITE_DIR%, else the default Steam path - which must exist (Linux, other drives: set the variable).</summary>
    static string DefaultResoniteDir()
    {
        if (Environment.GetEnvironmentVariable("RESONITE_DIR") is { Length: > 0 } dir) return dir;
        if (Directory.Exists(SteamResoniteDir)) return SteamResoniteDir;
        throw new ArgumentException($"Resonite install not found at the default {SteamResoniteDir}; set RESONITE_DIR or pass --resonite <dir>");
    }
}
